export type FileUploadResponse = {
  ok: boolean;
  fileId: string;
  fileName: string;
  mimeType: string;
  sizeBytes: number;
  obsidianRelativePath?: string | null;
};
