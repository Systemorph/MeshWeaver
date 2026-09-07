---
nodeType: WhatsNew
title: A database migration is rehearsed before it runs
category: Feature
date: 2026-09-07
---

The migration Job now asks its own image what it would do before letting it do it. An init
container runs the migration worker in rehearse mode: it reads the database's version, counts what
every pending repair would touch, executes nothing, and exits. Only when that succeeds does the
migration container start; a plan that fails to count stops the deploy before a single row moves.
The rehearsal shares the Job's image, configuration and ten-minute budget, so what it measures is
exactly what would run. Repairs written before today cannot be counted and are named as such;
every newer repair is declared as bulk steps that can be.

The behaviour is on by default (`migration.rehearse`) and needs a worker that knows the mode.
