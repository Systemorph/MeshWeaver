// 🚨 Timeout == error (Doc/Architecture/TimeoutIsError): every test in this assembly FAILS when a
// product-side timeout terminal fires during it, unless the test declares that terminal with
// [ExpectsTimeoutTerminal]. This assembly was clean when the guard was armed — keep it that way by
// fixing a stall at its root, never by declaring it.
[assembly: MeshWeaver.Fixture.TimeoutTerminalsAreErrors]
