# PulsePal

**Your desktop knows what you're doing. PulsePal knows how you're feeling.**

An offline Windows tray companion demo built with C#, .NET 10, WinUI 3, Windows App SDK, CommunityToolkit.Mvvm, dependency injection and hosted services. The companion appears for briefings, focus protection and recovery prompts; the developer controls are not the primary experience.

> PulsePal is not a medical device.
>
> All wellness information shown in demo mode is synthetic.

## Run locally

Requires Windows 10 version 2004 or newer, x64, the .NET 10 SDK and Windows development build tools. Visual Studio with the WinUI application development components is recommended. The first restore downloads NuGet dependencies; the running application needs no network connection, credentials, Azure resources or wearable devices.

```powershell
dotnet restore .\PulsePal.sln
dotnet build .\PulsePal.sln -c Release -p:Platform=x64
.\PulsePal.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\PulsePal.App.exe
```

Alternatively open `PP_code.slnx` (or `PulsePal.sln`) in Visual Studio, select **x64**, set **PulsePal.App** as the startup project, and run. The app is unpackaged and does not require MSIX installation.

For the already-published presentation build, run `.\artifacts\PulsePal\PulsePal.App.exe --demo-controls`. This opens the companion and developer controls without rebuilding or downloading anything.

The cyan tray icon may be inside Windows' hidden-icons overflow. Click it to show the companion; right-click for focus, breathing, developer controls and Exit. Closing the companion or developer controls leaves the tray app running. Use **Exit** to shut down and save state.

## Demo walkthrough

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

The default `DemoEngine` uses `SyntheticWearableProvider`. `IWearableProvider.GetSampleAsync` receives work context, a timestamp and cancellation. Inject another provider into `DemoEngine` to replace sample acquisition without rewriting the rule engine. `ReplayWearableProvider` supports reproducible recorded JSON sequences. Garmin and Oura adapters deliberately throw `NotSupportedException`: they do not imply an implemented live integration or require credentials.

Metrics include heart rate, resting heart rate, HRV, stress, steps, calories, activity, sleep stages, readiness, recovery, oxygen saturation, respiration, body battery and a synthetic focus estimate. Missing metrics are nullable. HRV is represented in milliseconds, heart rate in beats/minute, respiration in breaths/minute, sleep/activity in minutes, oxygen saturation in percent and normalized scores on a 0-100 scale. Different manufacturers have different proprietary definitions: this is a normalized demo schema, not a claim of API compatibility. FocusScore and burnout risk are PulsePal heuristics, not physiological measurements, diagnoses or standard wearable outputs.

Simulation uses bounded correlated trends rather than independent random values. Sleep stages sum to total sleep, sleep affects the day's scores, meeting load affects stress, and movement/breathing affect recovery. Accelerated scenario transitions are for demonstration, not clinical forecasts. Reasons explain the inputs behind each decision; a burnout-risk score represents sustained workload/recovery imbalance in the demo, not a medical prediction.

All character art is original, with a cyan holographic technology aesthetic. The portrait is isolated in the app's UI controls for replacement; normal, focused, happy, encouraging, concerned, thinking, celebrating and resting states remain separate from the wellness model.

## Local data and privacy

State is saved to `%LOCALAPPDATA%\PulsePal\state.json` using `System.Text.Json`: preferences, scenario, companion state and bounded demo history. Writes use same-directory atomic replacement. Invalid data produces a visible error rather than silent success; malformed existing files are backed up before replacement. Data is local plain-text JSON, not encrypted. Shut down the app before deleting the directory to reset local data.

## Tests and distribution

```powershell
dotnet test .\PulsePal.Tests\PulsePal.Tests.csproj -c Release
dotnet publish .\PulsePal.App\PulsePal.App.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o .\artifacts\PulsePal
.\artifacts\PulsePal\PulsePal.App.exe
```

Use `--demo-controls` to open the developer window at launch. `--smoke-test --smoke-test-full` runs a real UI exercise including all scenarios, profiles and expressions, persistent peak prompts, focus filtering, break cancellation/hiding, the full 60-second breathing cycle, snooze and auto-hide, then exits. It takes approximately two minutes and writes `%LOCALAPPDATA%\PulsePal\smoke-test.json`, `smoke-test.png`, and `profile-*.png`. Run it with other PulsePal instances closed; it uses local demo state and restores the original preferences and scenario afterward. The five-minute screen-break timing and exact-once completion for every activity are also covered by deterministic monotonic-clock unit tests.

Distribute the entire publish folder, not just the executable: WinUI resources and native runtime files must remain together. The build is intentionally untrimmed. No signing certificate or cloud deployment is required for this local demo.
