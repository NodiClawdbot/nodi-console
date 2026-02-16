import { AfterViewInit, Component, NgZone } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import DOMPurify from 'dompurify';
import { marked } from 'marked';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ChatApiService } from './chat-api.service';
import { VoiceApiService } from './voice-api.service';

type Msg = { at: string; from: string; text: string };

type VoiceMode = 'walkietalki' | 'silentchat';

type TtsVoice = 'alloy' | 'nova' | 'shimmer' | 'echo' | 'fable' | 'onyx';

type ThemeMode = 'auto' | 'light' | 'dark';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css'
})
export class AppComponent implements AfterViewInit {
  private mdConfigured = false;
  status = 'ready';
  text = '';
  messages: Msg[] = [];
  sending = false;
  sessionKey: string | null = null;

  pendingFiles: File[] = [];
  uploadInfo: string | null = null;

  voiceMode: VoiceMode = 'walkietalki';
  ttsVoice: TtsVoice = 'nova';

  theme: ThemeMode = 'auto';

  effectiveTheme: 'light' | 'dark' = 'light';

  playingTts = false;

  showSettings = false;

  recording = false;
  recordStatus: string | null = null;
  private mediaRecorder?: MediaRecorder;
  private chunks: BlobPart[] = [];
  private recordStartedAtMs: number | null = null;

  // Capture → Obsidian inbox
  showCapture = false;
  captureText = '';
  captureSaving = false;
  captureStatus: string | null = null;
  captureRecording = false;
  private captureRecorder?: MediaRecorder;
  private captureChunks: BlobPart[] = [];
  private captureStartedAtMs: number | null = null;
  private captureStream?: MediaStream;

  private audioCtx?: AudioContext;
  private audioUnlocked = false;
  private currentAudioSource?: AudioBufferSourceNode;
  private currentHtmlAudio?: HTMLAudioElement;

  constructor(
    private zone: NgZone,
    private sanitizer: DomSanitizer,
    private chatApi: ChatApiService,
    private voiceApi: VoiceApiService
  ) {
    // Use app-specific keys to avoid clashing with other Nodi UIs.
    this.sessionKey = window.localStorage.getItem('ncb.sessionKey') || window.localStorage.getItem('nodi.sessionKey');
    if (this.sessionKey) {
      this.status = 'ready (session resumed)';
    }

    const savedMode = (window.localStorage.getItem('ncb.voiceMode') || window.localStorage.getItem('nodi.voiceMode') || '').trim().toLowerCase();
    if (savedMode === 'silentchat' || savedMode === 'walkietalki') {
      this.voiceMode = savedMode as VoiceMode;
    }

    const savedVoice = (window.localStorage.getItem('ncb.ttsVoice') || window.localStorage.getItem('nodi.ttsVoice') || '').trim().toLowerCase();
    const allowed: TtsVoice[] = ['alloy', 'nova', 'shimmer', 'echo', 'fable', 'onyx'];
    if (allowed.includes(savedVoice as TtsVoice)) {
      this.ttsVoice = savedVoice as TtsVoice;
    }

    const savedTheme = (window.localStorage.getItem('ncb.theme') || window.localStorage.getItem('nodi.theme') || '').trim().toLowerCase();
    if (savedTheme === 'auto' || savedTheme === 'light' || savedTheme === 'dark') {
      this.theme = savedTheme as ThemeMode;
    }
    this.updateEffectiveTheme();
    this.applyTheme();
  }

  async send() {
    const t = this.text.trim();
    if (this.sending) return;
    if (!t && this.pendingFiles.length === 0) return;

    await this.sendTextAsUser(t, this.pendingFiles);
    this.text = '';
    this.pendingFiles = [];
  }

  openCapture() {
    this.showCapture = true;
    this.captureStatus = null;
    if (!this.captureText.trim()) this.captureText = '';
  }

  closeCapture() {
    if (this.captureSaving) return;
    this.showCapture = false;
    this.captureStatus = null;
    if (this.captureRecording) {
      this.stopCaptureRecording();
    }
  }

  async saveCapture() {
    const t = (this.captureText || '').trim();
    if (!t) return;
    if (this.captureSaving) return;

    this.captureSaving = true;
    this.captureStatus = 'Sparar till inbox…';

    try {
      const res = await this.chatApi.captureInbox(t);
      const title = (res.title || '').trim();
      const summary = (res.summary || '').trim();
      const where = (res.file || '').trim();

      const msg = [
        'Inbox: sparat i Obsidian.',
        where ? `Fil: ${where}` : null,
        title ? `Rubrik: ${title}` : null,
        summary ? `Sammanfattning: ${summary}` : null,
      ].filter(Boolean).join('\n');

      this.messages = [...this.messages, { at: new Date().toISOString(), from: 'system', text: msg }];

      this.captureText = '';
      this.showCapture = false;
      this.captureStatus = null;
      this.status = 'ready';
    } catch (err: any) {
      const msg = String(err?.message ?? err ?? 'unknown error');
      this.captureStatus = `Kunde inte spara: ${msg}`;
    } finally {
      this.captureSaving = false;
    }
  }

  toggleCaptureRecording() {
    if (this.captureRecording) {
      this.stopCaptureRecording();
    } else {
      void this.startCaptureRecording();
    }
  }

  private async startCaptureRecording() {
    if (this.captureRecording) return;

    // Unlock audio + stop any ongoing TTS to reduce feedback.
    void this.ensureAudioUnlocked();
    this.stopTts();

    this.captureStatus = 'Spelar in…';

    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    this.captureStream = stream;

    const rec = new MediaRecorder(stream, { mimeType: 'audio/webm' });
    this.captureRecorder = rec;
    this.captureChunks = [];

    rec.ondataavailable = (ev) => {
      if (ev.data && ev.data.size > 0) {
        this.zone.run(() => this.captureChunks.push(ev.data));
      }
    };

    rec.onstop = () => {
      this.zone.run(() => {
        void (async () => {
          // stop mic
          try {
            this.captureStream?.getTracks().forEach((t) => t.stop());
          } catch {}
          this.captureStream = undefined;

          const blob = new Blob(this.captureChunks, { type: 'audio/webm' });
          this.captureChunks = [];

          const durationMs = this.captureStartedAtMs ? Date.now() - this.captureStartedAtMs : null;
          this.captureStartedAtMs = null;

          if ((durationMs != null && durationMs < 450) || blob.size < 6000) {
            this.captureStatus = 'Ingen röst uppfattades.';
            return;
          }

          try {
            this.captureStatus = 'Transkriberar…';
            const text = await this.voiceApi.stt(blob);
            const cleaned = (text || '').trim();
            if (!cleaned) {
              this.captureStatus = 'Ingen text uppfattades.';
              return;
            }

            // Append to captureText with a blank line if needed.
            if (this.captureText.trim().length > 0) {
              this.captureText = (this.captureText.trimEnd() + '\n\n' + cleaned).trim();
            } else {
              this.captureText = cleaned;
            }

            this.captureStatus = null;
          } catch (err: any) {
            const msg = String(err?.message ?? err ?? 'unknown error');
            this.captureStatus = `STT-fel: ${msg}`;
          }
        })();
      });
    };

    rec.start();
    this.captureStartedAtMs = Date.now();
    this.captureRecording = true;
  }

  private stopCaptureRecording() {
    if (!this.captureRecording) return;
    this.captureRecording = false;
    try {
      this.captureRecorder?.stop();
    } catch {}
    this.captureRecorder = undefined;
  }

  private async sendTextAsUser(t: string, files?: File[]) {
    const fileLabel = (files && files.length > 0) ? `\n[${files.length} bilaga/bilagor]` : '';

    // optimistic append
    this.messages = [...this.messages, { at: new Date().toISOString(), from: 'you', text: (t || '(bilaga)').trim() + fileLabel }];

    this.sending = true;
    this.status = 'thinking…';

    try {
      let fileIds: string[] | undefined = undefined;
      if (files && files.length > 0) {
        this.status = 'uploading…';
        this.uploadInfo = `Laddar upp ${files.length} fil(er)…`;
        fileIds = [];
        for (const f of files) {
          const u = await this.chatApi.uploadFile(f);
          fileIds.push(u.fileId);

          // Optional: show where we saved it in Obsidian
          if (u.obsidianRelativePath) {
            this.messages = [
              ...this.messages,
              { at: new Date().toISOString(), from: 'system', text: `Sparad i Obsidian: ${u.obsidianRelativePath}` },
            ];
          }
        }
        this.uploadInfo = null;
      }

      this.status = 'thinking…';
      const res = await this.chatApi.send(t, {
        sessionKey: this.sessionKey ?? undefined,
        timeoutMs: 60000,
        fileIds,
      });

      this.sessionKey = res.sessionKey;
      window.localStorage.setItem('ncb.sessionKey', this.sessionKey);

      const assistant = (res.text || '').trim();
      if (assistant.length > 0) {
        this.messages = [...this.messages, { at: new Date().toISOString(), from: 'nodi', text: assistant }];

        if (this.voiceMode === 'walkietalki') {
          // best-effort TTS playback
          void this.playTts(assistant);
        }
      }

      this.status = 'ready';
    } catch (err: any) {
      const msg = String(err?.message ?? err ?? 'unknown error');
      this.messages = [...this.messages, { at: new Date().toISOString(), from: 'system', text: `Error: ${msg}` }];
      this.status = 'error';
    } finally {
      this.sending = false;
      this.uploadInfo = null;
    }
  }

  async onFilesSelected(ev: Event) {
    const input = ev.target as HTMLInputElement | null;
    const list = input?.files;
    if (!list || list.length === 0) return;

    const next: File[] = Array.from(list);

    // reset input so selecting same file again re-triggers change
    if (input) input.value = '';

    const accepted: File[] = [];
    const rejected: string[] = [];

    for (const f of next) {
      // Limit: 25 MB per file (server also validates)
      if (f.size > 25 * 1024 * 1024) {
        rejected.push(`${f.name}: >25MB`);
        continue;
      }

      // Guard: reject extremely small images (often fails in vision models)
      if (f.type.startsWith('image/')) {
        try {
          const bmp = await createImageBitmap(f);
          const w = bmp.width;
          const h = bmp.height;
          bmp.close();

          if (w < 32 || h < 32) {
            rejected.push(`${f.name}: för liten bild (${w}×${h})`);
            continue;
          }
        } catch {
          // If we can't decode the image, let the server/model deal with it.
        }
      }

      accepted.push(f);
    }

    if (accepted.length > 0) {
      this.pendingFiles = [...this.pendingFiles, ...accepted];
    }

    if (rejected.length > 0) {
      this.messages = [
        ...this.messages,
        {
          at: new Date().toISOString(),
          from: 'system',
          text: `Vissa filer ignorerades:\n- ${rejected.join('\n- ')}`,
        },
      ];
    }
  }

  removePendingFile(i: number) {
    this.pendingFiles = this.pendingFiles.filter((_, idx) => idx !== i);
  }

  private micPointerDown = false;

  onMicPointerDown(e: PointerEvent) {
    if (this.sending) return;
    // Only react to primary button / touch.
    if ((e as any).button != null && (e as any).button !== 0) return;

    e.preventDefault();

    // Capture pointer so we reliably get pointerup even if the finger/mouse leaves the button.
    (e.currentTarget as HTMLElement | null)?.setPointerCapture?.(e.pointerId);

    // Unlock audio on user gesture (needed for autoplay policies) + allow "barge-in".
    void this.ensureAudioUnlocked();
    this.stopTts();

    this.micPointerDown = true;
    void this.startRecording();
  }

  onMicPointerUp(e: PointerEvent) {
    if (!this.micPointerDown) return;
    e.preventDefault();
    this.micPointerDown = false;
    this.stopRecording();
  }

  onMicPointerCancel(e: PointerEvent) {
    if (!this.micPointerDown) return;
    e.preventDefault();
    this.micPointerDown = false;
    this.cancelRecording();
  }

  onMicPointerLeave(_e: PointerEvent) {
    // If pointer capture is not supported and we lose the pointer, stop recording.
    if (!this.micPointerDown) return;
    if (this.mediaRecorder && !('setPointerCapture' in HTMLElement.prototype)) {
      this.micPointerDown = false;
      this.stopRecording();
    }
  }

  async startRecording() {
    if (this.recording) return;

    this.recordStatus = 'Lyssnar… (håll inne)';

    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    const rec = new MediaRecorder(stream, { mimeType: 'audio/webm' });
    this.mediaRecorder = rec;
    this.chunks = [];

    // MediaRecorder callbacks may fire outside Angular change detection depending on browser.
    // Always re-enter Angular zone when mutating UI state.
    rec.ondataavailable = (ev) => {
      if (ev.data && ev.data.size > 0) {
        this.zone.run(() => this.chunks.push(ev.data));
      }
    };

    rec.onstop = () => {
      this.zone.run(() => {
        void (async () => {
          // stop mic
          stream.getTracks().forEach((t) => t.stop());

          const blob = new Blob(this.chunks, { type: 'audio/webm' });
          this.chunks = [];

          const durationMs = this.recordStartedAtMs ? Date.now() - this.recordStartedAtMs : null;
          this.recordStartedAtMs = null;

          // Heuristic: ignore very short / tiny recordings to reduce Whisper "silence hallucinations"
          // like "thank you" / "bye bye".
          if ((durationMs != null && durationMs < 450) || blob.size < 6000) {
            this.status = 'ready';
            this.recordStatus = 'Ingen röst uppfattades.';
            return;
          }

          try {
            this.status = 'transcribing…';
            this.recordStatus = 'Transkriberar…';

            const text = await this.voiceApi.stt(blob);
            const cleaned = (text || '').trim();

            // Filter common hallucinations on silence/near-silence.
            const lc = cleaned.toLowerCase();
            const looksLikeSilenceHallucination =
              (durationMs != null && durationMs < 1200) &&
              (lc === 'thank you' || lc === 'thank you.' || lc === 'thanks' || lc === 'see you' || lc === 'bye' || lc === 'bye bye' || lc === 'see you bye bye');

            if (!cleaned || looksLikeSilenceHallucination) {
              this.status = 'ready';
              this.recordStatus = 'Ingen text uppfattades.';
              return;
            }

            this.recordStatus = null;
            await this.sendTextAsUser(cleaned);
          } catch (err: any) {
            const msg = String(err?.message ?? err ?? 'unknown error');
            this.messages = [...this.messages, { at: new Date().toISOString(), from: 'system', text: `STT Error: ${msg}` }];
            this.status = 'error';
            this.recordStatus = 'Kunde inte transkribera.';
          }
        })();
      });
    };

    rec.start();
    this.recordStartedAtMs = Date.now();
    this.recording = true;
    this.status = 'recording…';
  }

  stopRecording() {
    if (!this.recording) return;
    this.recording = false;
    this.mediaRecorder?.stop();
    this.mediaRecorder = undefined;
    this.status = 'uploading…';
  }

  cancelRecording() {
    if (!this.recording) return;
    this.recording = false;

    // Stop recording and drop chunks without sending.
    try {
      this.mediaRecorder?.stop();
    } catch {
      // ignore
    }
    this.mediaRecorder = undefined;
    this.chunks = [];

    this.status = 'ready';
    this.recordStatus = 'Avbruten.';
  }

  setVoiceMode(mode: VoiceMode) {
    this.voiceMode = mode;
    window.localStorage.setItem('ncb.voiceMode', mode);

    if (mode === 'walkietalki') {
      void this.ensureAudioUnlocked();
    } else {
      this.stopTts();
    }
  }

  toggleSettings() {
    this.showSettings = !this.showSettings;
  }

  setTtsVoice(v: TtsVoice) {
    this.ttsVoice = v;
    window.localStorage.setItem('ncb.ttsVoice', v);
  }

  setTheme(mode: ThemeMode | string) {
    const v = String(mode).toLowerCase();
    const next: ThemeMode = (v === 'light' || v === 'dark' || v === 'auto') ? (v as ThemeMode) : 'auto';

    this.theme = next;
    window.localStorage.setItem('ncb.theme', next);
    this.updateEffectiveTheme();
    this.applyTheme();
  }

  ngAfterViewInit(): void {
    // Ensure body exists before applying theme (Mobile Safari is picky).
    this.updateEffectiveTheme();
    this.applyTheme();

    try {
      window.matchMedia?.('(prefers-color-scheme: dark)')?.addEventListener?.('change', () => {
        this.updateEffectiveTheme();
        // If theme is auto, ensure we reflect device changes.
        if (this.theme === 'auto') this.applyTheme();
      });
    } catch {
      // ignore
    }
  }

  private updateEffectiveTheme() {
    const prefersDark = (() => {
      try {
        return window.matchMedia?.('(prefers-color-scheme: dark)')?.matches ?? false;
      } catch {
        return false;
      }
    })();

    this.effectiveTheme = (this.theme === 'dark') ? 'dark' : (this.theme === 'light') ? 'light' : (prefersDark ? 'dark' : 'light');
  }

  private applyTheme() {
    // Use data-theme on <html> (and body as fallback) so CSS can override prefers-color-scheme.
    const html = document.documentElement;
    const body = document.body;

    if (this.theme === 'auto') {
      html.removeAttribute('data-theme');
      body?.removeAttribute('data-theme');
      return;
    }

    html.setAttribute('data-theme', this.theme);
    body?.setAttribute('data-theme', this.theme);
  }

  autoGrow(ev: Event) {
    const ta = ev.target as HTMLTextAreaElement | null;
    if (!ta) return;

    // Reset height so it can shrink, then grow to content.
    ta.style.height = '0px';
    const next = Math.min(220, Math.max(76, ta.scrollHeight));
    ta.style.height = `${next}px`;
  }

  stopTts() {
    // Stop WebAudio
    try {
      this.currentAudioSource?.stop();
    } catch {
      // ignore
    }
    this.currentAudioSource = undefined;

    // Stop HTMLAudio
    try {
      if (this.currentHtmlAudio) {
        this.currentHtmlAudio.pause();
        this.currentHtmlAudio.currentTime = 0;
      }
    } catch {
      // ignore
    }
    this.currentHtmlAudio = undefined;

    this.playingTts = false;
  }

  private async ensureAudioUnlocked() {
    try {
      if (!this.audioCtx) {
        // WebAudio is the most reliable way to satisfy autoplay policies once resumed by a user gesture.
        this.audioCtx = new (window.AudioContext || (window as any).webkitAudioContext)();
      }
      if (this.audioCtx.state !== 'running') {
        await this.audioCtx.resume();
      }
      this.audioUnlocked = true;
    } catch {
      // ignore (we'll still try HTMLAudio fallback)
    }
  }

  private async playTts(text: string) {
    // stop any current playback
    this.stopTts();

    this.playingTts = true;

    try {
      const blob = await this.voiceApi.tts(text, this.ttsVoice);

      // Prefer WebAudio if we managed to unlock it.
      if (this.audioCtx && this.audioUnlocked) {
        const buf = await blob.arrayBuffer();
        const audioBuffer = await this.audioCtx.decodeAudioData(buf.slice(0));
        const src = this.audioCtx.createBufferSource();
        src.buffer = audioBuffer;
        src.connect(this.audioCtx.destination);
        this.currentAudioSource = src;

        await new Promise<void>((resolve) => {
          src.onended = () => resolve();
          src.start(0);
        });

        this.currentAudioSource = undefined;
        this.playingTts = false;
        return;
      }

      // Fallback: HTMLAudio (may be blocked by autoplay policies)
      const url = URL.createObjectURL(blob);
      const audio = new Audio(url);
      this.currentHtmlAudio = audio;
      audio.onended = () => {
        URL.revokeObjectURL(url);
        this.currentHtmlAudio = undefined;
        this.playingTts = false;
      };
      await audio.play();
    } catch {
      // best-effort
      this.playingTts = false;
    }
  }

  renderMarkdown(text: string): SafeHtml {
    // Configure marked once (keep it deterministic).
    if (!this.mdConfigured) {
      marked.setOptions({ gfm: true, breaks: true });
      this.mdConfigured = true;
    }

    const raw = marked.parse(text || '') as string;
    const clean = DOMPurify.sanitize(raw, { USE_PROFILES: { html: true } });
    return this.sanitizer.bypassSecurityTrustHtml(clean);
  }

  newSession() {
    this.sessionKey = null;
    window.localStorage.removeItem('ncb.sessionKey');
    window.localStorage.removeItem('nodi.sessionKey');
    this.messages = [];
    this.status = 'ready (new session)';
  }
}
