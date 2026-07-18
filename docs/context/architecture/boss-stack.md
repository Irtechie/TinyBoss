# Boss Stack Boundaries

TinyBoss is the local executor. PitBoss is the dispatcher. OmniBoss, PixelBoss, Discord, and tray surfaces should call PitBoss rather than TinyBoss directly.

```text
Surface -> PitBoss API -> TinyBoss pipe/tcp WebSocket -> Windows terminals/apps
```

Related repos:

- `E:\Dev\AI\OmniBoss`: web chat surface.
- `E:\Dev\AI\pitboss`: dispatcher, memory, backlog, providers, TinyBoss API bridge.
- `E:\Dev\AI\TerminalBoss`: terminal surface TinyBoss can launch or observe.
- `E:\Dev\AI\PixelBoss`: phone surface.
