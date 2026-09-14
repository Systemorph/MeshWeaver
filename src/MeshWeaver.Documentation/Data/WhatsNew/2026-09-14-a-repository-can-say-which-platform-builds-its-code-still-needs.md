---
Name: A repository can say which platform builds its code still needs
Category: Feature
Description: Repositories resolve which sealed platform build to compile against rather than pinning one, and nothing said which builds their own code could still be built with — so lifting one hand-set build turned every open pull request red on a missing name nobody had touched. A repository can now declare its oldest workable build, and the rule that keeps pull requests on a vouched build no longer reads its own test data.
Icon: Checkmark
Order: -20260914
---

# A repository can say which platform builds its code still needs

Repositories that build against the platform do not pin a version — each CI run resolves which
sealed platform build to compile against. Two rules shaped that choice. One kept pull requests on
the newest build the repository's own `main` had passed, so a bad platform build turns one branch
red instead of every open pull request. The other let an operator name a build explicitly during an
incident.

Between them they answered which build is *trusted*, and which build to *take*. Neither answered
which builds the repository's own code can still be **compiled** with — and that becomes a separate
question the moment a file there starts calling something the platform only recently gained.

It did. A file adopted a new platform type while an explicit build was named, so its pull request
was green. The naming was later removed — a one-field change, with no review and no trace in any
diff — and the resolver fell back to the newest build `main` had passed, fifty builds older than the
one the code needed. Every open pull request turned red on a missing name in a file none of their
authors had touched.

**A repository can now write down the oldest platform build its code compiles against, and why**, in
a small file beside the resolver. A run that resolves below it stops and says so: which build it
refused, the floor, the reason the floor exists, and the only honest way past it — remove the
adoption and lower the floor in the same change. It applies to every route, including an explicitly
named build: naming a build chooses among those that can compile the tree, and cannot make an older
one contain something it never had. Declaring nothing means no floor, so nothing changes for a
repository that does not want one; a file that cannot be read is an error rather than a shrug,
because a rule quietly ignored is worse than no rule.

**And the rule that keeps pull requests on a vouched build stopped trusting a list it cannot
distinguish.** That rule reads its answer back out of a short note each run publishes about the
build it chose. Where a repository runs the resolver's own self-tests in the same job, those tests
published notes of the same shape carrying invented build numbers — and the reader took whichever
came back first. It now reads *all* of them and, when two disagree, **skips that run and says which
numbers it saw**, rather than choosing one. Other runs still answer, so a spoiled run costs a data
point instead of producing a wrong answer.

One related note for anyone naming a build by hand: *newest sealed* is necessary and not sufficient.
A build can be sealed and still have been published nowhere, and the resolver is what detects that —
`--verify-source` refuses such a build rather than compiling against content no machine can load.
Resolve a candidate with that flag before naming it, because a build that fails there fails in the
first job of every subsequent run, for the whole repository.
