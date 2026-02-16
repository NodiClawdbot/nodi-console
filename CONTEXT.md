# nodi-console — CONTEXT

## Mål
Bygga en **lokal always-on live-app** för Nodi med fokus på:
- Voice (PTT + transkribering)
- Approvals (kanban-liknande vy för "godkänn → kör")
- Ops/status (LLM, gateway, tailscale serve)

## Principer
- Lokal först (loopback + Tailscale Serve)
- Minimal friktion: snabb start, tydliga paneler
- Återanvändbar byggkloss för kommande nivåer (kan lyftas in i andra projekt)

## V1 scope
1) Voice panel:
   - PTT, transcript
   - "Skicka till": Chat / Inbox / Router
2) Approvals panel:
   - Läs "🤖 Nodi routing (förslag).md"
   - Drag/drop mellan kolumner eller enkla knappar
   - Trigger dispatch (endast godkända)
3) Ops panel:
   - Visa senaste ops-check + aktiv primary

## Stack (förslag)
- Frontend: Angular
- Backend: ASP.NET Core Minimal API + SignalR
- Lokal DB: SQLite

## Nästa steg
- Skapa GitHub-repo: NodiClawdbot/nodi-console
- Scaffold: Angular frontend + .NET backend + docker-compose
- Lägg länk i nodi-dashboard (Projekt-kort + Links)
