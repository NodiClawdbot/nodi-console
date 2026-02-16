# nodi-console

Local live console for Nodi: voice (PTT), approvals, ops/status.

## Ports
- Frontend: http://127.0.0.1:4300
- Backend:   http://127.0.0.1:5300

## Run

```bash
cd /Users/johanlisemark/clawd/projects/nodi-console

docker compose up -d --build
curl -s http://127.0.0.1:5300/health
```

## Notes
- This repo is currently bootstrapped by copying the working `nodi-clawdbot` app and adjusting ports.
- We will iterate it into a proper multi-panel console (voice / approvals / ops).
