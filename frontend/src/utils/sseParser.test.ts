import { describe, expect, it } from 'vitest';
import { parseSseLine, splitSseBuffer } from './sseParser';

describe('parseSseLine', () => {
  it('parses a valid SSE data line', () => {
    const line = 'data: {"type":"conversationId","conversationId":"conv-123"}';

    const parsed = parseSseLine(line);

    expect(parsed).toEqual({
      type: 'conversationId',
      data: {
        conversationId: 'conv-123',
      },
    });
  });

  it('returns null for non-data lines', () => {
    expect(parseSseLine('event: message')).toBeNull();
    expect(parseSseLine('')).toBeNull();
  });

  it('returns null for malformed JSON', () => {
    const parsed = parseSseLine('data: {"type":"chunk",');
    expect(parsed).toBeNull();
  });
});

describe('splitSseBuffer', () => {
  it('returns complete lines and keeps trailing partial line in remaining buffer', () => {
    const [lines, remaining] = splitSseBuffer('data: 1\ndata: 2\npartial');

    expect(lines).toEqual(['data: 1', 'data: 2']);
    expect(remaining).toBe('partial');
  });

  it('handles trailing newline by returning empty remaining buffer', () => {
    const [lines, remaining] = splitSseBuffer('a\nb\n');

    expect(lines).toEqual(['a', 'b']);
    expect(remaining).toBe('');
  });
});
