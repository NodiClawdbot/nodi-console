import { Injectable } from '@angular/core';

type ChatSendRequest = {
  text: string;
  sessionKey?: string;
  thinking?: string;
  timeoutMs?: number;
  fileIds?: string[];
};

type ChatSendResponse = {
  ok: boolean;
  sessionKey: string;
  runId: string;
  text?: string | null;
};

type InboxCaptureResponse = {
  ok: boolean;
  file?: string;
  title?: string;
  summary?: string;
};

function backendBaseUrl(): string {
  const host = window.location.hostname || 'localhost';
  if (host === 'localhost' || host === '127.0.0.1') {
    return 'http://127.0.0.1:5300';
  }
  return `${window.location.origin}/nodi-api`;
}

@Injectable({ providedIn: 'root' })
export class ChatApiService {
  private baseUrl = backendBaseUrl();

  async uploadFile(file: File): Promise<{ fileId: string; obsidianRelativePath?: string | null }> {
    const fd = new FormData();
    fd.append('file', file, file.name);

    const res = await fetch(`${this.baseUrl}/api/files/upload`, {
      method: 'POST',
      body: fd,
    });

    if (!res.ok) {
      const t = await res.text().catch(() => '');
      throw new Error(`Upload error ${res.status}: ${t || res.statusText}`);
    }

    const j = (await res.json()) as any;
    return { fileId: j.fileId, obsidianRelativePath: j.obsidianRelativePath };
  }

  async send(
    text: string,
    opts?: { sessionKey?: string; thinking?: string; timeoutMs?: number; fileIds?: string[] },
  ): Promise<ChatSendResponse> {
    const body: ChatSendRequest = {
      text,
      sessionKey: opts?.sessionKey,
      thinking: opts?.thinking,
      timeoutMs: opts?.timeoutMs,
      fileIds: opts?.fileIds,
    };

    const res = await fetch(`${this.baseUrl}/api/chat/send`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });

    if (!res.ok) {
      const t = await res.text().catch(() => '');
      throw new Error(`Backend error ${res.status}: ${t || res.statusText}`);
    }

    return (await res.json()) as ChatSendResponse;
  }

  async captureInbox(text: string): Promise<InboxCaptureResponse> {
    const res = await fetch(`${this.baseUrl}/api/inbox/capture`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text }),
    });

    if (!res.ok) {
      const t = await res.text().catch(() => '');
      throw new Error(`Inbox capture error ${res.status}: ${t || res.statusText}`);
    }

    return (await res.json()) as InboxCaptureResponse;
  }
}
