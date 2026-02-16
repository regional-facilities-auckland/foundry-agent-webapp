import { describe, expect, it } from 'vitest';
import type { IAnnotation } from '../types/chat';
import { parseContentWithCitations } from './citationParser';

describe('parseContentWithCitations', () => {
  it('returns original content when no annotations are provided', () => {
    const result = parseContentWithCitations('Hello world');

    expect(result.processedText).toBe('Hello world');
    expect(result.citations).toEqual([]);
  });

  it('replaces textToReplace placeholders and deduplicates citations', () => {
    const placeholder = '【4:0†spec.pdf】';
    const annotation: IAnnotation = {
      type: 'file_citation',
      label: 'spec.pdf',
      fileId: 'file-1',
      textToReplace: placeholder,
    };

    const content = `First ${placeholder} second ${placeholder}`;
    const result = parseContentWithCitations(content, [annotation]);

    expect(result.processedText).toBe('First [1] second [1]');
    expect(result.citations).toHaveLength(1);
    expect(result.citations[0].index).toBe(1);
    expect(result.citations[0].count).toBe(1);
    expect(result.citations[0].annotation.label).toBe('spec.pdf');
  });

  it('creates placeholder citation for unmatched assistants-style citation pattern', () => {
    const result = parseContentWithCitations('See source 【13†unknown.pdf】 now', []);

    expect(result.processedText).toBe('See source 【13†unknown.pdf】 now');
    expect(result.citations).toEqual([]);

    const withAnnotationFallback = parseContentWithCitations('See source 【13†unknown.pdf】 now', [
      {
        type: 'file_citation',
        label: 'another.pdf',
      },
    ]);

    expect(withAnnotationFallback.processedText).toBe('See source [1] now');
    expect(withAnnotationFallback.citations[0].annotation.label).toBe('unknown.pdf');
  });
});
