---
Name: Every instance reports what it runs
Category: Feature
Description: A portal now files its own inventory with the control instance — platform build, commit, framework identity, update policy and every module's pinned coordinate — once it has booted and hourly after, so the fleet view shows what each instance runs without anyone running a script.
Icon: ClipboardTaskListLtr
Order: -20260906
---

# Every instance reports what it runs

Which instance runs which build, and which modules at which commit, used to depend on an operator
running a script on each portal — and nobody ever had. Every portal that is named as a deployment now
reports itself: once its default install has settled and then every hour, it sends the control
instance the platform version and commit it serves, the framework identity its module bundles are
keyed on, the self-update policy it follows, and one row per module with the repository, branch and
commit or package version it is pinned to.

On the control instance the Fleet view and the Deployments board fold those reports into one table:
a module whose build differs between two instances, or that one instance is missing, is a row you
can see rather than a question you have to ask. A report that could not read one of its sources says
so in the record instead of arriving short, and a rejected or failed report is logged with the reason
and retried on the next tick.

Operators name an instance with `Hosting:Deployment` and point it at the control instance with
`Hosting:ReportTo` and a signing secret; the manual reporting script remains as a fallback. Details in
[DeploymentInventory](/Doc/Architecture/DeploymentInventory).
