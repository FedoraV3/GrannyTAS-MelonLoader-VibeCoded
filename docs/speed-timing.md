# slow-motion navigation follow-up — 0.3.2

reported symptom: granny is faster relative to the slowed player and overshoots
her path. the previous implementation lowered the render cap but left engine
`timeScale=1`. the player used a capture-pinned delta while native navigation
was never explicitly given a slow-motion scale.

ida confirms that `AI_Granny.FixedUpdate` assigns ordinary walk/run speeds to a
`NavMeshAgent`; it does not scale those values for tas playback.
[call-site excerpts](ida-speed-evidence.txt). the actual navmesh integrator is
engine code outside the inspected game function. a difference in engine clock
handling is a working explanation for the report, not a measured root cause.

the timing controller now uses:

```text
fps cap = max(1, round(tick rate * requested speed))
engine timescale = fps cap / tick rate, rounded to the nearest power of two (0.4.0)
capture delta = (1 / tick rate) / engine timescale
physics delta = 1 / physics rate
```

0.4.0: the power-of-two rounding makes `capture delta × timescale` exactly
`1 / tick rate` in double precision too — with 29/60 at 0.48x only the float
product was exact, so the engine's double clock drifted against a 1x replay.
See [replay-sync.md](replay-sync.md).

unity documents capture delta as scaled by timescale, so their product remains
one simulated frame. [unity 2022.3 capture-time contract](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Time-captureDeltaTime.html).
using the rounded cap's speed avoids a second mismatch: at 165 ticks/s, a 0.56x
request yields 92 fps and a timescale of 92/165. native systems following scaled
real time then receive the same nominal frame duration as the capture-pinned
player when the frame cap is met. granny's walk/run speed fields are unchanged.

pause keeps both timescale and capture delta zero. stepping uses 1x engine
scale and a tick-rate frame cap for its simulated frame, then returns to pause.
the runtime delta check skips transition frames and reports sustained deviations
from `1/tick rate` in the log and panel. it checks update timing, not navmesh
movement or the complete fixed-update schedule.

speed and turbo setters now respect the existing macro lock, covering hotkeys,
panel controls and direct callers. the panel disables those controls. recording
retains the speed chosen before starting; playback stays at 1x. pause and step
remain available. locking prevents accidental mid-run changes; changing pacing
does not inherently corrupt an input macro when every clock is synchronized.

64 regression checks pass, including slow/fast clock compensation, unchanged
physics delta, speed/turbo locks, unlock-on-stop, slow-motion stepping and runtime
delta warnings. the release build succeeds with zero warnings/errors. tests use
engine boundary stubs and do not verify unity's native navigation integration.

runtime acceptance: test a fresh route at 1x, 0.56x and 0.1x with identical
simulation/physics rates. compare granny's speed relative to the player and her
turning at corners; check for a clock mismatch log. re-record any route affected
by the old movement mismatch. full world restoration, fixed-step phase,
performance below the frame cap, and uncapped turbo remain replay limitations.
