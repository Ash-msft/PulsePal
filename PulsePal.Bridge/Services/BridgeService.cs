using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PulsePal.Connected;

namespace PulsePal.Bridge.Services;

internal sealed class BridgeService(TimeProvider clock) : IBridgeService
{
    private readonly object gate = new();
    private readonly Dictionary<(string Source, HealthMetric Metric, string Id), ReceivedHealthRecord> records = [];
    private readonly Dictionary<(IPAddress Address, string Operation), (DateTimeOffset Start, int Count)> rates = [];
    private CategoryReadStatus[] categories = [];
    private bool running;
    private string? endpoint, pin, pairingString, deviceName;
    private byte[]? challengeHash, tokenHash;
    private DateTimeOffset certificateExpiry, challengeExpiry, sessionExpiry;
    private Pending? pending;

    private sealed class Pending(Guid id, string name, byte[] pollHash, DateTimeOffset expires)
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public byte[] PollHash { get; } = pollHash;
        public DateTimeOffset Expires { get; } = expires;
        public PairingState State { get; set; } = PairingState.Pending;
        public string? Token { get; set; }
    }

    public bool IsRunning { get { lock (gate) { SweepCore(); return running; } } }
    public string? PairingString { get { lock (gate) { SweepCore(); return pairingString; } } }
    public string? PairedDeviceName { get { lock (gate) { SweepCore(); return deviceName; } } }
    public IReadOnlyList<PendingDevice> PendingDevices
    {
        get
        {
            lock (gate)
            {
                SweepCore();
                return pending is { State: PairingState.Pending } p
                    ? new[] { new PendingDevice(p.Id, p.Name, p.Expires) } : Array.Empty<PendingDevice>();
            }
        }
    }
    public IReadOnlyList<ReceivedHealthRecord> Records
    {
        get { lock (gate) { SweepCore(); return records.Values.ToArray(); } }
    }
    public IReadOnlyList<CategoryReadStatus> Categories
    {
        get { lock (gate) { SweepCore(); return categories.ToArray(); } }
    }

    public void Start(string address, string certificatePin, DateTimeOffset expiry)
    {
        lock (gate)
        {
            ClearCore();
            rates.Clear();
            endpoint = address;
            pin = certificatePin;
            certificateExpiry = expiry;
            running = true;
            RefreshCore();
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            running = false;
            ClearCore();
            rates.Clear();
            endpoint = pin = null;
        }
    }

    public void Sweep() { lock (gate) SweepCore(); }
    private void SweepCore()
    {
        var now = clock.GetUtcNow();
        if (now >= certificateExpiry || (tokenHash is not null && now >= sessionExpiry))
        {
            ClearCore();
            return;
        }
        if (now >= challengeExpiry) { challengeHash = null; pairingString = null; }
        if (pending is { } p && now >= p.Expires)
        {
            // An unclaimed token must not leave a paired but unreachable desktop.
            if (p.Token is not null) ClearCore();
            else pending = null;
        }
    }

    private void ClearCore()
    {
        records.Clear();
        categories = [];
        challengeHash = tokenHash = null;
        pairingString = deviceName = null;
        if (pending is not null) pending.Token = null;
        pending = null;
    }

    public void Revoke() { lock (gate) ClearCore(); }
    public void DeleteRecords() { lock (gate) { SweepCore(); records.Clear(); categories = []; } }
    public void Refresh()
    {
        lock (gate)
        {
            SweepCore();
            if (!running || clock.GetUtcNow() >= certificateExpiry || tokenHash is not null)
                throw new InvalidOperationException("Pairing is unavailable.");
            pending = null;
            RefreshCore();
        }
    }
    private void RefreshCore()
    {
        var challenge = Secret();
        challengeHash = Hash(challenge);
        challengeExpiry = Min(clock.GetUtcNow().AddMinutes(2), certificateExpiry);
        pairingString = JsonSerializer.Serialize(new PairingPayload(1, endpoint!, pin!, challenge, challengeExpiry),
            ConnectedJson.CreateOptions());
    }

    public PendingPairingResponse Pair(CreatePairingRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            SweepCore();
            BridgeValidator.Require(HealthMetadata.IsValid(request.DeviceName, 80) &&
                BridgeValidator.Text(request.Challenge, 64));
            if (!running || tokenHash is not null || pending is { State: PairingState.Pending })
                throw new BridgeRequestException(409);
            if (!Matches(request.Challenge, challengeHash)) throw new BridgeRequestException(400);
            challengeHash = null;
            pairingString = null;
            var pollSecret = Secret();
            pending = new Pending(Guid.NewGuid(), request.DeviceName, Hash(pollSecret), challengeExpiry);
            return new(pending.Id, pollSecret, pending.Expires);
        }
    }

    public void Approve(Guid id)
    {
        lock (gate)
        {
            SweepCore();
            if (pending is not { State: PairingState.Pending } p || p.Id != id) return;
            p.Token = Secret();
            tokenHash = Hash(p.Token);
            sessionExpiry = certificateExpiry;
            deviceName = p.Name;
            p.State = PairingState.Approved;
        }
    }
    public void Reject(Guid id)
    {
        lock (gate)
        {
            SweepCore();
            if (pending is { State: PairingState.Pending } p && p.Id == id) p.State = PairingState.Rejected;
        }
    }

    public PairingStatusResponse Poll(PairingStatusRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            SweepCore();
            BridgeValidator.Require(request.RequestId != Guid.Empty && BridgeValidator.Text(request.PollSecret, 64));
            if (pending is not { } p || p.Id != request.RequestId || !Matches(request.PollSecret, p.PollHash))
                return new(PairingState.Expired, null);
            var token = p.Token;
            p.Token = null;
            return new(p.State, token);
        }
    }

    public bool Authenticate(string? authorization)
    {
        lock (gate) { SweepCore(); return AuthenticateCore(authorization); }
    }
    private bool AuthenticateCore(string? authorization) =>
        running && authorization is { Length: 50 } &&
        authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
        Matches(authorization[7..], tokenHash);

    public SyncRecordsResponse Sync(string? authorization, SyncRecordsRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            SweepCore();
            if (!AuthenticateCore(authorization)) throw new BridgeRequestException(401);
            var now = clock.GetUtcNow();
            BridgeValidator.Validate(request, now);
            var accepted = 0;
            foreach (var record in request.Records)
            {
                var key = (record.SourceApp, record.Metric, record.RecordId);
                if (records.TryGetValue(key, out var old) && old.Record.Version >= record.Version) continue;
                if (!records.ContainsKey(key) && records.Count == 2048)
                    records.Remove(records.MinBy(p => p.Value.ReceivedAt).Key);
                records[key] = new(record with
                {
                    MeasuredAt = record.MeasuredAt.ToUniversalTime(),
                    EndAt = record.EndAt?.ToUniversalTime()
                }, now);
                accepted++;
            }
            categories = request.Categories.ToArray();
            return new(accepted, records.Count, now);
        }
    }

    public void Disconnect(string? authorization, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            SweepCore();
            if (!AuthenticateCore(authorization)) throw new BridgeRequestException(401);
            ClearCore();
        }
    }

    public bool AllowRequest(IPAddress? address, string operation)
    {
        lock (gate)
        {
            SweepCore();
            if (!running || address is null) return false;
            var now = clock.GetUtcNow();
            foreach (var key in rates.Where(p => now - p.Value.Start >= TimeSpan.FromMinutes(1)).Select(p => p.Key).ToArray())
                rates.Remove(key);
            var keyToUse = (address, operation);
            var limit = operation switch { "pair" => 10, "poll" => 60, _ => 120 };
            if (rates.TryGetValue(keyToUse, out var window))
            {
                if (window.Count >= limit) return false;
                rates[keyToUse] = (window.Start, window.Count + 1);
            }
            else
            {
                if (rates.Count >= 128) return false;
                rates[keyToUse] = (now, 1);
            }
            return true;
        }
    }

    private static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));
    private static bool Matches(string text, byte[]? hash) => hash is not null &&
        CryptographicOperations.FixedTimeEquals(Hash(text), hash);
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
