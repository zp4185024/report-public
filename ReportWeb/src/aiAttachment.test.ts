import { beforeAll, describe, expect, it, vi } from 'vitest';
import {
  AiAttachmentLimits,
  aiAttachmentLabel,
  readAiAttachment,
  toAiChatAttachments,
  validateAiAttachment,
  validateAiAttachmentCount,
  type AiAttachmentDraft,
} from './aiAttachment';

/**
 * 最小 FileReader 桩：vitest 默认 node 环境无 FileReader，
 * 以 readAsDataURL 同步实现 data URL 语义（btoa + 前缀），覆盖被测纯函数所需行为。
 */
class FakeFileReader {
  /** 读取结果（data URL）。 */
  result: string | null = null;

  /** 错误回调占位；本桩不产生错误。 */
  onerror: (() => void) | null = null;

  /** 成功回调引用。 */
  onload: (() => void) | null = null;

  /**
   * 以 data URL 形式同步读取 Blob 文本内容。
   * @param blob - 待读取的 Blob。
   */
  readAsDataURL(blob: Blob): void {
    void blob.text().then((text) => {
      this.result = `data:application/octet-stream;base64,${btoa(unescape(encodeURIComponent(text)))}`;
      this.onload?.();
    });
  }
}

beforeAll(() => {
  vi.stubGlobal('FileReader', FakeFileReader);
});

/**
 * 构造 File 对象：以 UTF-8 写入指定文本内容，模拟用户选择的附件。
 * @param name - 文件名。
 * @param content - 文本内容。
 * @returns File 实例。
 */
function makeFile(name: string, content: string): File {
  return new File([content], name, { type: 'application/octet-stream' });
}

describe('validateAiAttachment', () => {
  it('rejects unsupported extensions', () => {
    const error = validateAiAttachment(makeFile('报告.docx', 'x'));
    expect(error).toContain('类型不受支持');
  });

  it('rejects oversized images beyond 5MB', () => {
    const file = makeFile('报表截图.png', 'x');
    Object.defineProperty(file, 'size', { value: AiAttachmentLimits.imageMaxBytes + 1 });
    const error = validateAiAttachment(file);
    expect(error).toContain('超过 5MB 上限');
  });

  it('rejects oversized xlsx beyond 10MB', () => {
    const file = makeFile('费用.xlsx', 'x');
    Object.defineProperty(file, 'size', { value: AiAttachmentLimits.documentMaxBytes + 1 });
    const error = validateAiAttachment(file);
    expect(error).toContain('超过 10MB 上限');
  });

  it('accepts supported types within limits', () => {
    expect(validateAiAttachment(makeFile('报表截图.png', 'x'))).toBeNull();
    expect(validateAiAttachment(makeFile('费用.xlsx', 'x'))).toBeNull();
    expect(validateAiAttachment(makeFile('说明.PDF', 'x'))).toBeNull();
    expect(validateAiAttachment(makeFile('photo.webp', 'x'))).toBeNull();
  });
});

describe('validateAiAttachmentCount', () => {
  it('allows up to 3 attachments per message', () => {
    expect(validateAiAttachmentCount(0, 3)).toBeNull();
    expect(validateAiAttachmentCount(1, 2)).toBeNull();
  });

  it('rejects beyond 3 attachments per message', () => {
    const error = validateAiAttachmentCount(2, 2);
    expect(error).toContain('最多 3 个附件');
  });
});

describe('readAiAttachment', () => {
  it('reads file into base64 without data prefix and keeps metadata', async () => {
    const file = makeFile('费用汇总.xlsx', '参考内容');
    const draft = await readAiAttachment(file);
    expect(draft.fileName).toBe('费用汇总.xlsx');
    expect(draft.sizeBytes).toBeGreaterThan(0);
    expect(draft.dataBase64).not.toContain('data:');
    expect(draft.dataBase64.length).toBeGreaterThan(0);
    // base64 可逆：解码后应还原原文本（node 环境用 Buffer 解码）
    const decoded = Buffer.from(draft.dataBase64, 'base64').toString('utf8');
    expect(decoded).toBe('参考内容');
  });
});

describe('aiAttachmentLabel', () => {
  it('formats file name with human readable size', () => {
    const draft: AiAttachmentDraft = {
      fileName: '费用汇总.xlsx',
      contentType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
      sizeBytes: 2048,
      dataBase64: 'aGk=',
    };
    expect(aiAttachmentLabel(draft)).toBe('费用汇总.xlsx（2 KB）');
  });

  it('formats megabyte sizes with one decimal', () => {
    const draft: AiAttachmentDraft = {
      fileName: '大表.xlsx',
      contentType: 'application/octet-stream',
      sizeBytes: 1.5 * 1024 * 1024,
      dataBase64: 'aGk=',
    };
    expect(aiAttachmentLabel(draft)).toBe('大表.xlsx（1.5 MB）');
  });
});

describe('toAiChatAttachments', () => {
  it('maps drafts to request payload fields', () => {
    const payloads = toAiChatAttachments([
      { fileName: 'a.png', contentType: 'image/png', sizeBytes: 3, dataBase64: 'AA==' },
    ]);
    expect(payloads).toHaveLength(1);
    expect(payloads[0]).toEqual({
      fileName: 'a.png',
      contentType: 'image/png',
      sizeBytes: 3,
      dataBase64: 'AA==',
    });
  });
});
