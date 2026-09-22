# PulsePal

**Your desktop knows what you're doing. PulsePal knows how you're feeling.**

An offline Windows tray companion demo built with C#, .NET 10, WinUI 3, Windows App SDK, CommunityToolkit.Mvvm, dependency injection and hosted services. The companion appears for briefings, focus protection and recovery prompts; the developer controls are not the primary experience.

> PulsePal is not a medical device.
>
> All wellness information shown in demo mode is synthetic.

## Phone-health prototype (feature branch)

`feature/phone-health-sync` adds an **opt-in, separate connected-health window** and native Android/iPhone companion projects. The offline demo remains synthetic; phone readings never enter its stress, fatigue, focus or burnout rules.

- **Android 14+ and Samsung phones running Android 14+:** read heart-rate and blood-pressure records from built-in **Health Connect**, after category-specific read permission. Samsung Health or another source must actually write the measurements to Health Connect. Android 9-13 reports unsupported in this first implementation.
- **iPhone:** a native **HealthKit** companion reads heart rate and correlated blood-pressure measurements. Native deployment requires a Mac, compatible Xcode and HealthKit-enabled signing. Source/managed compilation is not proof of a working signed iPhone app.
- **Stress:** unsupported, not estimated from heart rate. BP requires actual recorded measurements from a supported source; the phone app does not measure BP.
- **Freshness:** manual foreground **Sync now**, not continuous wearable streaming. The desktop shows original timestamps, sources, receipt times and older-record labels. Phone/wearable synchronization determines what is available.

Open **Connected health** from the tray or companion menu. Opening the window starts no listener. Select a trusted private IPv4 interface and explicitly start it, transfer the short-lived pairing payload privately to the phone, compare the request IDs on both screens, and approve on Windows. HTTPS uses the exact certificate pin carried by the pairing payload; no blanket certificate-trust bypass or cloud account is used. Pairing credentials and received records are session-only, not written into the demo's JSON history. Closing connected health stops its listener and clears its session.

See **[ConnectedHealth.txt](ConnectedHealth.txt)** for build/install commands, pairing, platform limitations, privacy boundaries and the physical-device acceptance checklist. This is a development prototype, not a validated health-monitoring product. There is no claim of physical wearable/iPhone validation.

## Run locally

Requires Windows 10 version 2004 or newer, x64, the .NET 10 SDK and Windows development build tools. Visual Studio with the WinUI application development components is recommended. The first restore downloads NuGet dependencies; the offline demo needs no network connection, credentials, Azure resources or wearable devices. Optional connected health requires a supported phone and a trusted local network.

```powershell
dotnet restore .\PulsePal.sln
dotnet build .\PulsePal.sln -c Release -p:Platform=x64
.\PulsePal.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\PulsePal.App.exe
```

Alternatively open `PP_code.slnx` (or `PulsePal.sln`) in Visual Studio, select **x64**, set **PulsePal.App** as the startup project, and run. The app is unpackaged and does not require MSIX installation.

For the already-published presentation build, run `.\artifacts\PulsePal\PulsePal.App.exe --demo-controls`. This opens the companion and developer controls without rebuilding or downloading anything.

The cyan tray icon may be inside Windows' hidden-icons overflow. Click it to show the companion; its menu offers **A day with PulsePal…**, **Comparison & Session story** and **Meet <companion>**. Right-click the tray for presentation, focus, recovery, profile and developer controls, and Exit. Closing a window leaves the tray app running. Use **Exit** to shut down and save state.

## Seven demo-presentation features

1. **Accessible guided presenter.** Open **A day with PulsePal…** from the companion menu (also available from the tray). Opening it does not start a tour. Named controls, keyboard-accessible actions, headings and polite status announcements support presentation access. **Start…** asks for confirmation before resetting the current simulated session, then automatically runs seven steps: Briefing → Focus → Interruptions → Meeting stress → Recovery → Comparison → Summary. Briefing, Focus and Comparison each default to 8 seconds; Interruptions takes 12 seconds. Meeting stress waits at least 8 seconds and then as long as needed for the actual correlated synthetic peak (at least 90/100 for 10 seconds). **Next** can shorten ordinary narrative steps, but cannot invent a peak, skip unresolved recovery or finish a running break. **Pause/Resume** freezes/resumes scenario sampling, tour time, focus accounting and the recovery clock during the tour. The guide owns focus/scenario controls while active. **Stop** cancels unfinished recovery without benefit, ends focus and restores ordinary timing; the Session story remains available.
2. **Opt-in accelerated recovery.** **Accelerated demo** defaults off and is never persisted. Every activity previews for **15 actual seconds**, representing its catalog duration: breathing 60 seconds, screen break 300 seconds, water 60 seconds or movement 120 seconds. Accelerated breathing is a **non-guided illustration**, not sped-up breathing instruction. With acceleration off, normal full-duration recovery applies. At the Recovery step, an 8-second selection window precedes automatic start using the saved preferred break unless another is chosen; **Start selected recovery** starts it sooner. A session-only choice does not overwrite preferences. Acceleration and choice cannot change while paused or during a break; acceleration does not speed up stress readiness.
3. **Snapshot-based comparison.** **Comparison & Session story** shows pre-break and completion snapshots: analysis stress score, wearable recovery score and heart rate (bpm), with actual after-minus-before deltas. Missing/non-finite values and their deltas are marked unavailable; unchanged or worsening values are not rewritten as improvements. Cancellation creates no success comparison or completion benefit. Synthetic changes are not evidence of a health outcome or proof of a break's effect.
4. **Session story and totals.** The in-memory ledger reports actual focus time (including an active session), completed focus sessions, deferred and urgent-allowed synthetic notifications during focus, and completed/cancelled breaks. Completed breaks retain individual comparisons and totals grouped by activity and real-time/accelerated mode. Actual elapsed and represented catalog duration are distinct; guided paused time is excluded. These are session-only counts, not calendar-day totals, saved long-term analytics or measured productivity gains.
5. **Companion introductions.** Applying a different profile introduces that companion; **Meet <companion>** replays the introduction. Automatic introductions defer during recovery, an active peak prompt, a paused tour or snooze. Explicit Meet uses a noninterrupting banner during recovery/peak/pause, preserving the current prompt/timer; as a user-requested action it can replay during snooze.
6. **Scripted, exactly-once interruptions.** The guide delivers an FYI and group chat for deferral, followed by a manager approval, meeting reminder and customer escalation allowed through focus. Manual advancement delivers remaining events from the departing step without duplicating them. Only demo notifications are involved; recovery ends focus and releases the queue.
7. **Repeatable fresh-session reset.** **Reset…** also confirms first. Start/Reset reset the seeded synthetic runtime, trend/sleep burden, history, cooldown/snooze, notification queues, focus, timers, peak state, comparisons, event-delivery tracking and session counters while preserving saved preferences and companion identity. Reset returns to idle ordinary mode with acceleration off; use Start to run again. This does not reset replay/live data: guided reset and acceleration require the synthetic provider.

### Recommended live presentation

1. Show the synthetic-data disclaimer, choose a profile/preferred break, and use **Meet**.
2. Open **A day with PulsePal…**. Optionally enable **Accelerated demo** for a short presentation; leave it off for real-duration recovery. Click **Start…** and confirm the fresh-session reset.
3. Let focus and interruptions run automatically. Pause to explain deferred versus urgent items, then Resume. Allow correlated stress to establish the peak; Next cannot force it.
4. At Recovery, choose an alternative within 8 seconds or let the saved default start. Let the timer finish; an accelerated screen break represents five minutes but takes only 15 unpaused seconds.
5. Read the actual comparison and Session story, distinguishing elapsed from represented time. Use **Stop** for ordinary mode or confirmed **Reset…** for a clean repeat. Close with the privacy/health limitations below.

## Manual demo walkthrough

Stop the guide before using ordinary scenario/focus controls.

1. Launch for the personalized morning briefing. Open the tray menu's developer controls to see the synthetic metrics and explainable decisions.
2. Start a focus session. Simulate a low-priority email: it queues without interruption. Simulate an escalation: it is allowed through. End focus to see actual duration, delayed-item count and urgent-item count, with released items in notification history.
3. Select Rising Stress or Meeting Overload. Allow several two-second samples for stress to trend. Early elevated stress produces a gentle nudge; sustained near-maximum stress produces a persistent break-choice prompt rather than another fleeting message. This is a synthetic wellness cue, not an emergency diagnosis.
4. Choose guided breathing (one minute, 4-second inhale / 2-second hold / 6-second exhale), away from the screen (five minutes), a water break (one minute), or gentle movement (two minutes). The timer must finish before a synthetic recovery benefit applies. Cancelling grants no benefit; the app cannot verify that you actually drank water, moved or left the screen.
5. Compare Healthy Day, Deep Focus Session, Rising Stress, Meeting Overload, Recovery After Break and Poor Sleep Day. Walking increases activity; poor sleep influences readiness, fatigue and focus throughout the simulated day.

Use **Remind Later** to snooze wellness interruptions, including peak-stress prompts. Focus suppresses nonurgent wellness prompts, while prolonged high stress and explicitly simulated priority notifications can still surface. Peak-stress prompts remain visible until addressed or the elevated condition resolves; they do not bypass your snooze. Starting a recovery break ends the focus session and releases its queued demo notifications so away time is not counted as focused work. Every active break timer stays visible until you choose **Hide timer**; reopen the companion from the tray to see its countdown. Hiding is not cancelling.

Peak stress means a demo analysis score of at least 90/100 sustained for 10 seconds. It can escalate beyond an earlier mild nudge without waiting for that nudge's cooldown, but never bypasses snooze. Repeated peak prompts are limited to once every two minutes, and the condition clears after ten seconds below 80. These accelerated thresholds are for the demo, not clinical cutoffs.

## Your companion and preferences

Open **Profile & preferences** from the companion or tray menu. Choose between three original appearances with distinct voices: **Nova**, a calm cyan strategist; **Lumi**, a gentle soft-blue encourager with a crescent clip; and **Kairo**, an upbeat electric-blue teammate with a swept crest and headset. Preview every expression before applying. The chosen identity is kept across all eight emotional states.

Set your display name, go-to break, hydration reminders and popup duration in the same window. **Apply preferences** updates the companion and saves locally. Existing saved settings automatically receive the default Nova profile and five-minute screen-break preference. The blue/cyan application theme is retained.

**Attention Shield handles only the demo notification stream. It does not intercept, read or block real Windows, email or Teams notifications.** Desktop applications, meetings and task counts are mocked; there is no screen monitoring or Microsoft Graph connection. No telemetry or cloud AI is used.

## Architecture

| Project | Responsibility |
| --- | --- |
| `PulsePal.App` | WinUI views, MVVM state, original vector companion, native tray integration, animations, hosted sampling and UI orchestration |
| `PulsePal.Core` | Nullable wearable contracts, work context, explainable wellness rules, notification decisions and cooldown policy |
| `PulsePal.Infrastructure` | Correlated synthetic provider, replay provider, future adapter stubs, mock context, thread-safe demo orchestration and JSON persistence |
| `PulsePal.Tests` | xUnit coverage for simulation, scoring, focus/stress/fatigue risk, notification filtering, cooldowns, recovery, replay and storage |
| `PulsePal.Connected` | Shared real-record protocol, strict JSON, pairing policy and certificate-pinned foreground client |
| `PulsePal.Bridge` | Opt-in HTTPS receiver, desktop-approved pairing, bounded in-memory records and revocation |
| `PulsePal.Phone.Android` | Native Android app with read-only Android 14+ Health Connect access |
| `PulsePal.Phone.iOS` | Native UIKit app with read-only HealthKit access; build/deploy separately |

The default `DemoEngine` uses `SyntheticWearableProvider`. `IWearableProvider.GetSampleAsync` receives work context, a timestamp and cancellation. `ReplayWearableProvider` supports reproducible recorded JSON sequences. Provider injection replaces demo sample acquisition, but does not make the demo's accelerated rules appropriate for real health data. Connected health deliberately uses its own contracts and display instead. Garmin and Oura adapters deliberately throw `NotSupportedException`: they do not imply an implemented live integration or require credentials.

Metrics include heart rate, resting heart rate, HRV, stress, steps, calories, activity, sleep stages, readiness, recovery, oxygen saturation, respiration, body battery and a synthetic focus estimate. Missing metrics are nullable. HRV is represented in milliseconds, heart rate in beats/minute, respiration in breaths/minute, sleep/activity in minutes, oxygen saturation in percent and normalized scores on a 0-100 scale. Different manufacturers have different proprietary definitions: this is a normalized demo schema, not a claim of API compatibility. FocusScore and burnout risk are PulsePal heuristics, not physiological measurements, diagnoses or standard wearable outputs.

Simulation uses bounded correlated trends rather than independent random values. Sleep stages sum to total sleep, sleep affects the day's scores, meeting load affects stress, and movement/breathing affect recovery. Accelerated scenario transitions are for demonstration, not clinical forecasts. Reasons explain the inputs behind each decision; a burnout-risk score represents sustained workload/recovery imbalance in the demo, not a medical prediction.

All character art is original, with a cyan holographic technology aesthetic. The portrait is isolated in the app's UI controls for replacement; normal, focused, happy, encouraging, concerned, thinking, celebrating and resting states remain separate from the wellness model.

### Presentation source map and programmatic APIs

- `PulsePal.App\AppController.Presentation.cs`: `ShowPresenter`, `StartPresentation`, `PausePresentation`, `ResumePresentation`, `NextPresentation`, `ResetPresentation`, `StopPresentation`, `StartSelectedPresentationRecovery`, `ShowSessionStory` and `MeetCompanion`; exposes `Tour`, `AcceleratedDemo`, `SelectedPresentationBreak`, `SessionStory` and `LastComparison`. UI Start/Reset commands confirm first; direct lifecycle APIs do not display confirmation.
- `PulsePal.App\PresenterWindow.cs` and `ViewModels\PresenterViewModel.cs`: accessible controls, confirmation dialogs, recovery selection, comparison and session-story bindings.
- `PulsePal.Core\GuidedTour.cs` and `GuidedEvents.cs`: gated seven-step progression and exactly-once synthetic notification script.
- `PulsePal.Core\SessionClock.cs`, `SessionLedger.cs` and `RecoveryComparison.cs`: pause-aware monotonic time, in-memory accounting and nullable snapshot deltas. `RecoverySession.cs` separates actual and represented duration.
- `PulsePal.Infrastructure\DemoEngine.cs`: synthetic-only presentation lifecycle, `Clock`, `Ledger`, `SupportsPresentation` and `PeakStressEstablished`; `SyntheticWearableProvider.Reset` resets synthetic runtime state.
- `PulsePal.App\App.xaml.cs` and `AppController.PresentationSmoke.cs`: isolated smoke startup/options and the two-cycle presentation exercise.

## Local data and privacy

State is saved to `%LOCALAPPDATA%\PulsePal\state.json` using `System.Text.Json`: preferences, scenario, companion state and bounded demo history. Writes use same-directory atomic replacement. Invalid data produces a visible error rather than silent success; malformed existing files are backed up before replacement. Data is local plain-text JSON, not encrypted. Shut down the app before deleting the directory to reset local data.

The presentation ledger, comparisons and acceleration setting are not persisted. While the guided clock is active, saves preserve the pre-tour ordinary scenario/history with current preferences rather than saving the synthetic tour as ordinary history. Test-only path overrides below do not change the normal default state location.

Connected records, phone tokens and pending pairing secrets are volatile and separate from this demo store. Revocation, stopping/closing the connected listener or exiting clears them. The application does not write connected request bodies or health readings to its logs. OS paging, crash dumps, clipboard software and a compromised device remain outside that guarantee; use only a trusted network and devices. Read [ConnectedHealth.txt](ConnectedHealth.txt) before sharing any real data.

## Tests and distribution

```powershell
dotnet test .\PulsePal.Tests\PulsePal.Tests.csproj -c Release
dotnet publish .\PulsePal.App\PulsePal.App.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o .\artifacts\PulsePal
.\artifacts\PulsePal\PulsePal.App.exe
```

Use `--demo-controls` to open the developer window at launch. Run either desktop exercise from the workspace root:

```powershell
.\artifacts\PulsePal\PulsePal.App.exe --smoke-test-presentation
.\artifacts\PulsePal\PulsePal.App.exe --smoke-test --smoke-test-full
.\artifacts\PulsePal\PulsePal.App.exe --smoke-test-connected
```

`--smoke-test-presentation` exercises two tour cycles, including real correlated peak waits, 15-second recovery previews, pause/resume, reset, comparison and ledger checks, then exits. Allow roughly three minutes; stress is not fast-forwarded. The legacy full UI exercise takes approximately two minutes, covers all scenarios, profiles/expressions, peak prompts, focus filtering, cancellation/hiding, full 60-second breathing, snooze and auto-hide, then exits. Deterministic monotonic-clock tests also cover five-minute screen breaks and exact-once activity completion.

**All smoke modes (including `--smoke-test-connected`) isolate state by default** in a new `artifacts\presentation-smoke-<guid>` directory under the current working directory, not ordinary LocalAppData state. Outputs include `state.json`, diagnostics, and the corresponding `smoke-test-presentation.json`, `smoke-test-connected.json` or `smoke-test.json`; legacy UI runs also produce `smoke-test.png` and full-run `profile-*.png` captures. Reports and process exit status indicate success/failure. Connected smoke exercises the opt-in window, loopback HTTPS authorization and listener cleanup; it does not validate a phone or wearable.

Optional **test-only** overrides (choose a fresh state filename for each run):

```powershell
.\artifacts\PulsePal\PulsePal.App.exe --smoke-test-presentation --state-path .\artifacts\presentation-check-01\state.json --log-directory .\artifacts\presentation-check-01\logs
```

`--state-path` must name a new isolated file: smoke startup rejects an existing file or the ordinary state path. `--log-directory` redirects reports/logs and cannot be the ordinary user-state directory in smoke mode. Ordinary launches without overrides still use `%LOCALAPPDATA%\PulsePal\state.json`. Avoid interacting with other PulsePal windows during UI exercises; isolation means smoke runs no longer load or modify ordinary preferences/history.

The phone projects are intentionally outside the Windows solution build. Build them explicitly using the platform instructions in ConnectedHealth.txt. Automated bridge/client tests use test fixtures, not someone's health records; passing them does not demonstrate native permission flows or wearable-to-phone synchronization.

Distribute the entire publish folder, not just the executable: WinUI resources and native runtime files must remain together. The build is intentionally untrimmed. No signing certificate or cloud deployment is required for this local demo.
