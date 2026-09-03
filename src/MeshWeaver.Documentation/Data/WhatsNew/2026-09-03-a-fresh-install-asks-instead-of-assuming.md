---
Name: A fresh install now asks you to set it up
Category: Feature
Description: An instance with no database opens a setup page that asks which database, which sign-ins, which model keys and which modules — instead of refusing to start or being configured for you in advance.
Icon: Server
Order: -20260903
---

# A fresh install now asks you to set it up

Installing MeshWeaver used to mean deciding everything before the first start. The database, the way
people sign in and the model keys all had to be written into configuration files by hand, and an
instance that started without them simply refused to run.

A fresh instance now opens a **setup page** in the browser instead. It asks four questions:

- **Which database.** SQLite is pre-selected — a single file, with nothing else to install or keep
  running — and it is now a real choice rather than one that existed only in the code. PostgreSQL
  and the other backends this build ships are offered alongside it, with a place to paste a
  connection string.
- **How people sign in.** The built-in developer login, and any of Microsoft, Google, LinkedIn,
  Apple and GitHub, each with its client id and secret. At least one is required — an instance with
  no way in is one nobody can enter, including you.
- **Which model keys.** A key per AI provider, plus the endpoint used for search. Leaving the search
  endpoint blank is allowed but the page says what it costs: search then matches words rather than
  meaning, and nothing else would ever tell you.
- **Which modules.** What starts with the instance, and which packages to install on first boot.

Press Install and the instance restarts configured. Nothing is asked twice, and every answer stays
editable afterwards.

Secrets you type are encrypted before they are stored, under a key the instance creates for itself
if the deployment has not supplied one — and that key is kept in a separate file from the answers,
so the settings can be copied or backed up without carrying the credentials along. If a secret
cannot be encrypted, the page refuses rather than storing it in the clear.

The page is protected by a one-time token the instance prints to its own log when it starts, so
somebody who merely reaches the address cannot configure your portal before you do. On a local
install, `memex-local setup` fetches the token and opens the page for you.
