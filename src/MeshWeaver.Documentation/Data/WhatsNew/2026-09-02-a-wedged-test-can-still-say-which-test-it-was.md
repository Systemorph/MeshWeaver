---
Name: A wedged test can still say which test it was
Category: Fix
Description: The check that proves each test suite got its configured 30-second per-test limit now travels with the configuration itself, so it applies everywhere the platform's test setup is used instead of only inside the core repository.
Icon: Timer
Order: -20260902
---

# A wedged test can still say which test it was

When a test stops making progress, there are two ways it can end, and they are not equally useful.

If the suite is running with its configured **30-second per-test limit**, that one test is stopped,
marked failed, and everything it logged on the way down is written out. You get the name, the window,
and the last thing that happened before it stalled.

If that limit is missing, nothing stops the test. The whole run continues until the build system's
own wall-clock cap kills the entire process — and a killed process writes no results at all. The
transcript of the one test you needed to see is the specific thing that is lost. The failure that is
hardest to diagnose is the one that destroys its own evidence.

Which of those two happens is decided by a small configuration file reaching each suite's build
output. The platform's shared test setup puts it there, and a check runs after every build to confirm
it actually arrived — because when it does not, nothing looks wrong: the build succeeds, the tests
run, they simply run under different settings than anyone chose (unlimited parallelism, and no
per-test limit at all).

**That check was in a different file from the configuration it was checking.** The two are picked up
independently, so a project could take the configuration without taking the check — and every
repository built on top of the platform did exactly that. They got the setup and not the assurance
that it had worked: the check covered roughly fifteen test projects and missed more than seventy-five.

**The check now lives in the same file as the thing it checks**, so taking one always means taking the
other, with no change needed in any of the repositories that build on the platform. A separate test
fails if the two are ever pulled apart again.

Nothing about how tests run has changed. What changed is that the promise is now verified everywhere
it is made — and the difference shows up on the day something wedges, in whether the report names the
test or just says the run was killed.
