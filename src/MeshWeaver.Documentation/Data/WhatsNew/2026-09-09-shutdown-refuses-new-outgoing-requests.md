---
Name: Shutdown refuses new outgoing requests
Category: Fix
Description: A component that is already shutting down now refuses new outgoing requests immediately instead of starting work whose response may be cancelled by teardown.
Icon: CheckmarkCircle
Order: -20260909
---

Background work could start a new request after its owning component had begun shutting down.
The request passed the forwarding rule because its destination was another component, leaving
shutdown responsible for a new response it might cancel before it arrived.

The component now reports that it is shutting down before sending that new request. Responses
to previously accepted requests and traffic forwarded for other components can still pass while
existing work drains.
