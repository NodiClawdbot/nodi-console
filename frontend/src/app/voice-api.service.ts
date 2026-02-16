import { Injectable } from '@angular/core';

function backendBaseUrl(): string {
  const host = window.location.hostname || 'localhost';
  if (host === 'localhost' || host === '127.0.0.1') {
    return 'http://127.0.0.1:5300';
  }
  return `${window.location.origin}/nodi-api`;
}

@Injectable({ providedIn: 'root' })
export class VoiceApiService {
  private baseUrl = backendBaseUrl();

  async stt(blob: Blob): Promise<string> {
    const fd = new FormData();
    fd.append('file', blob, 'audio.webm');

    const res = await fetch(`${this.baseUrl}/api/stt`, {
      method: 'POST',
      body: fd,
    });

    if (!res.ok) {
      const t = await res.text().catch(() => '');
      throw new Error(`STT error ${res.status}: ${t || res.statusText}`);
    }

    const j = (await res.json()) as { ok: boolean; text?: string };
    return (j.text || '').trim();
  }

  async tts(text: string, voice: string = 'alloy'): Promise<Blob> {
    const res = await fetch(`${this.baseUrl}/api/tts`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text, voice }),
    });

    if (!res.ok) {
      const t = await res.text().catch(() => '');
      throw new Error(`TTS error ${res.status}: ${t || res.statusText}`);
    }

    return await res.blob();
  }
}
