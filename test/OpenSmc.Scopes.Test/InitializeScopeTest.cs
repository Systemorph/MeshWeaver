using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Scopes.Test;

public class InitializeScopeTest(ITestOutputHelper toh) : ScopesTestBase(toh)
{
    [Fact]
        public void TestInit()
        {
            var scopes = ScopeFactory.ForIdentities(Enumerable.Range(0, 5)).ToScopes<IInitializeScope>();
            foreach (var scope in scopes)
            {
                scope.IntProperty.Should().Be(scope.Identity);
            }
        }
    }
