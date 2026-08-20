"""A thin Home Assistant REST client, exposed to the local brain as ONE tool.

The gateway keeps the satellite's voice subscription (the ESPHome voice pipeline has one
owner); Home Assistant rides alongside as a tool — lights, switches, scenes, media — via
its long-lived-access-token REST API. Configure with HA_URL + HA_TOKEN; absent, the tool
is simply not offered.
"""

from __future__ import annotations

import aiohttp

_LIST_LIMIT = 40


class HomeAssistant:
    def __init__(self, base_url: str, token: str) -> None:
        self.base_url = base_url.rstrip("/")
        self._headers = {"Authorization": f"Bearer {token}"}
        self._http: aiohttp.ClientSession | None = None

    async def _session(self) -> aiohttp.ClientSession:
        if self._http is None:
            self._http = aiohttp.ClientSession(headers=self._headers)
        return self._http

    async def run(self, args: dict) -> str:
        action = str(args.get("action", "")).strip()
        if action == "list_entities":
            return await self._list_entities(str(args.get("domain", "")).strip())
        if action == "get_state":
            return await self._get_state(str(args.get("entity_id", "")).strip())
        if action == "call_service":
            return await self._call_service(str(args.get("domain", "")).strip(),
                                            str(args.get("service", "")).strip(),
                                            str(args.get("entity_id", "")).strip())
        return f"Unknown action: {action!r}. Use list_entities, get_state, or call_service."

    async def _list_entities(self, domain: str) -> str:
        http = await self._session()
        async with http.get(f"{self.base_url}/api/states") as response:
            response.raise_for_status()
            states = await response.json()
        rows = [f"{s['entity_id']}: {s.get('state')}"
                f" ({(s.get('attributes') or {}).get('friendly_name', '')})"
                for s in states
                if not domain or s["entity_id"].startswith(domain + ".")]
        if not rows:
            return f"No entities{f' in domain {domain}' if domain else ''}."
        clipped = rows[:_LIST_LIMIT]
        more = f" …and {len(rows) - len(clipped)} more" if len(rows) > len(clipped) else ""
        return "\n".join(clipped) + more

    async def _get_state(self, entity_id: str) -> str:
        if not entity_id:
            return "entity_id is required for get_state."
        http = await self._session()
        async with http.get(f"{self.base_url}/api/states/{entity_id}") as response:
            if response.status == 404:
                return f"No entity {entity_id}."
            response.raise_for_status()
            state = await response.json()
        attrs = state.get("attributes") or {}
        name = attrs.get("friendly_name", entity_id)
        return f"{name} is {state.get('state')}."

    async def _call_service(self, domain: str, service: str, entity_id: str) -> str:
        if not (domain and service):
            return "domain and service are required for call_service."
        http = await self._session()
        payload = {"entity_id": entity_id} if entity_id else {}
        async with http.post(f"{self.base_url}/api/services/{domain}/{service}",
                             json=payload) as response:
            if response.status >= 400:
                return f"Home Assistant refused {domain}.{service}: HTTP {response.status}."
        return f"Done: {domain}.{service}" + (f" on {entity_id}." if entity_id else ".")

    async def close(self) -> None:
        if self._http is not None:
            await self._http.close()
            self._http = None
