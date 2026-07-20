---
Name: Invited people reliably get their access the moment they sign up
Category: What's New
Description: A pending group or space invite now grants membership and access as soon as the invitee onboards — even on the multi-node portal where the sign-up event used to be missed
Icon: Sparkle
---

# Invites now land access on sign-up, every time

When you invite someone who doesn't have an account yet, the platform schedules their group membership and space access to apply the moment they onboard. On the distributed portal that hand-off could be missed: the invitee signed up, but their pending grants stayed stuck until the next restart — so they'd log in and find they had access to nothing.

The onboarding hand-off no longer depends on an in-memory event that can be dropped between nodes. It now watches for the new user through the database directly, so a pending invite fires reliably the instant the account is created — no restart, no manual fix-up. Existing pending invitations heal automatically.
