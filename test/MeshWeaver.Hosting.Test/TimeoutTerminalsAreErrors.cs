// 🚨 Timeout == error (Doc/Architecture/TimeoutIsError): every test in this assembly FAILS when a
// product-side timeout terminal fires during it, unless the test declares that terminal with
// [ExpectsTimeoutTerminal] because the timeout IS its subject. Fix a stall at its root, never by
// declaring it.
[assembly: MeshWeaver.Fixture.TimeoutTerminalsAreErrors]
