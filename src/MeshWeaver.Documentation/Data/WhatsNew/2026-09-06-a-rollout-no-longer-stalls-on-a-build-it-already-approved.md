---
Name: A rollout no longer stalls on a build it already approved
Category: Fix
Description: A portal pod that could not reach the build coordination node refused readiness and held the rollout on the previous image — even when the go-ahead for exactly that image was already recorded and readable.
Icon: CloudArrowUp
Order: -20260906
---

On 2026-09-06 two pods of one deployment refused to come up and held the rollout on the previous
image. The build they were waiting for had already been approved: the go-ahead for their exact
image was written on the build coordination node at 11:28:56Z, and they refused at 11:44:32Z and
11:49:37Z — about sixteen minutes later.

Nothing was wrong with the verdict. The pods simply had only one way of learning it. At boot each
pod *subscribes* to the coordination node, and those subscription requests went unanswered; when
that runs out of attempts the pod concludes it has verified nothing about this image and refuses
readiness. That refusal is correct in itself — a pod that verified nothing must never claim it
did — but it was being reached while a durable "yes" for that very image sat unread.

A pod now has the second door the rest of the protocol already had: when the subscription cannot be
established, it **reads** the coordination record directly and lets readiness follow what is
recorded there. The go-ahead is per-image, so only this pod's own build counts — one written for a
different image reads as no answer at all, which is what keeps an old pod serving safely while a
new image is still unproven.

Everything protective stays exactly as it was. No timeout was lengthened and no retry was added. If
neither door answers, the pod still refuses readiness and the rollout still holds — the underlying
connectivity fault is a real, separate problem, and it is still reported in full so it can be found
and fixed rather than quietly papered over.
