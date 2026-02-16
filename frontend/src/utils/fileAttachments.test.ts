import { describe, expect, it } from 'vitest';
import {
  createAttachmentMetadata,
  getEffectiveMimeType,
  validateDocumentFile,
  validateFile,
  validateFileCount,
  validateImageFile,
} from './fileAttachments';

describe('getEffectiveMimeType', () => {
  it('returns explicit browser MIME type when provided', () => {
    const file = new File(['x'], 'photo.png', { type: 'image/png' });
    expect(getEffectiveMimeType(file)).toBe('image/png');
  });

  it('falls back to extension mapping when MIME type is missing', () => {
    const file = new File(['# title'], 'notes.md');
    expect(getEffectiveMimeType(file)).toBe('text/markdown');
  });
});

describe('file validation', () => {
  it('rejects non-image files in validateImageFile', () => {
    const file = new File(['hello'], 'doc.txt', { type: 'text/plain' });
    const result = validateImageFile(file);

    expect(result.valid).toBe(false);
    expect(result.error).toContain('is not an image file');
  });

  it('rejects oversized documents in validateDocumentFile', () => {
    const tooLarge = new Uint8Array(20 * 1024 * 1024 + 1);
    const file = new File([tooLarge], 'manual.pdf', { type: 'application/pdf' });
    const result = validateDocumentFile(file);

    expect(result.valid).toBe(false);
    expect(result.error).toContain('Maximum file size is 20MB');
  });

  it('routes unsupported MIME types through validateFile', () => {
    const file = new File(['x'], 'archive.zip', { type: 'application/zip' });
    const result = validateFile(file);

    expect(result.valid).toBe(false);
    expect(result.error).toContain('not a supported file type');
  });

  it('enforces max file count', () => {
    const files = [
      new File(['a'], '1.txt', { type: 'text/plain' }),
      new File(['b'], '2.txt', { type: 'text/plain' }),
    ];

    const result = validateFileCount(files, 9);
    expect(result.valid).toBe(false);
    expect(result.error).toContain('Maximum 10 files allowed');
  });
});

describe('createAttachmentMetadata', () => {
  it('maps conversion results to attachment metadata', () => {
    const metadata = createAttachmentMetadata([
      {
        name: 'image.png',
        dataUri: 'data:image/png;base64,abc',
        mimeType: 'image/png',
        sizeBytes: 3,
      },
    ]);

    expect(metadata).toEqual([
      {
        fileName: 'image.png',
        fileSizeBytes: 3,
        dataUri: 'data:image/png;base64,abc',
      },
    ]);
  });
});
