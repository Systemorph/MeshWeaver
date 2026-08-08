---
Name: A rate-limited model now says so, instead of dumping HTTP headers into your thread
Category: What's New
Description: When a provider refuses a round — out of quota, or a server error — the thread now shows the reason in your language and names the model that ran, instead of pasting the raw transport error.
Icon: Sparkle
---

# A rate-limited model now says so, instead of dumping HTTP headers into your thread

Sometimes a round dies because the model deployment refuses it: it is out of quota,
or the provider itself is faulting. Until now the thread showed you the provider's
raw failure — the status line, the JSON body, and the complete block of HTTP
response headers, in English, whatever your language. It was the least readable
thing on the page, and it was the only account you had of what went wrong.

A refused round now ends with a sentence: the model is out of quota (or the
provider returned an error), in your language, naming the model that actually ran.
The raw transport detail hasn't been thrown away — it moves to the log, where an
operator can still see every byte of it, together with the status and both model
names.

There is a second half to this, and it is the one that used to be genuinely
confusing. If the model you picked has no usable credentials, your round is quietly
moved onto one that has — so the failure you were shown named a model you never
selected, with nothing anywhere saying why. When a substituted round fails, it now
says so in one sentence: which model you chose, and which one was used instead.

Errors that aren't the provider's are untouched. If a round fails because of a bug
or a tool, its own message still comes through word for word — that message is the
diagnosis, and replacing it with something tidier would only hide it.
