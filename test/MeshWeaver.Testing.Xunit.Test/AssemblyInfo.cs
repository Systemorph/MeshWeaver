// The whole point of this assembly: it runs on the execution host under test. Every case below is
// therefore also a live assertion that the host reports normally — a framework that mangled
// discovery or reporting would show up as this suite not running at all.
[assembly: Xunit.TestFramework(typeof(MeshWeaver.Testing.Xunit.MeshTestFramework))]
[assembly: Xunit.AssemblyFixture(typeof(MeshWeaver.Testing.Xunit.Test.DisposalAudit))]
