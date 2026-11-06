import type { AiChatAttachment } from './types';

/** 单条消息附件数量上限（spec §4）。 */
export const AiAttachmentLimits = {
  maxCount: 3,
  imageMaxBytes: 5 * 1024 * 1024,
  documentMaxBytes: 10 * 1024 * 1024,
} as const;

/** 图片类型白名单（按扩展名小写判定）。 */
const ImageExtensions = ['.png', '.jpg', '.jpeg', '.webp'] as const;

/** 文档类型白名单（按扩展名小写判定）。 */
const DocumentExtensions = ['.xlsx', '.pdf'] as const;

/** 待发送的附件草稿：File 引用 + base64 内容（不含 data: 前缀），随消息一次性内联提交。 */
export interface AiAttachmentDraft {
  fileName: string;
  contentType: string;
  sizeBytes: number;
  dataBase64: string;
}

/**
 * 取文件扩展名（小写，含点）；无扩展名返回空串。
 * @param fileName - 原始文件名。
 * @returns 小写扩展名（如 ".xlsx"）。
 */
function extensionOf(fileName: string): string {
  const dot = fileName.lastIndexOf('.');
  return dot < 0 ? '' : fileName.slice(dot).toLowerCase();
}

/**
 * 判断文件类型是否受支持：图片（png/jpg/jpeg/webp）或文档（xlsx/pdf）。
 * @param fileName - 原始文件名。
 * @returns 受支持返回 true。
 */
export function isAiAttachmentSupported(fileName: string): boolean {
  const extension = extensionOf(fileName);
  return (ImageExtensions as readonly string[]).includes(extension)
    || (DocumentExtensions as readonly string[]).includes(extension);
}

/**
 * 校验单个附件的类型与大小；与后端 PocAiReferenceMaterial.Validate 逐条对应，错误文案可直接展示。
 * @param file - 用户选择的文件。
 * @returns 第一个校验错误文案；合法返回 null。
 */
export function validateAiAttachment(file: File): string | null {
  const extension = extensionOf(file.name);
  if (!isAiAttachmentSupported(file.name)) {
    return `附件「${file.name}」类型不受支持，仅允许图片（png/jpg/jpeg/webp）、Excel（xlsx）与 PDF。`;
  }
  if ((ImageExtensions as readonly string[]).includes(extension) && file.size > AiAttachmentLimits.imageMaxBytes) {
    return `图片「${file.name}」超过 5MB 上限，请压缩后再上传。`;
  }
  if ((DocumentExtensions as readonly string[]).includes(extension) && file.size > AiAttachmentLimits.documentMaxBytes) {
    return `附件「${file.name}」超过 10MB 上限，请拆分后再上传。`;
  }
  return null;
}

/**
 * 校验待发送附件集合的数量上限；超限返回错误文案，合法返回 null。
 * @param existingCount - 已在附件条中的附件数量。
 * @param incomingCount - 本次想追加的附件数量。
 * @returns 错误文案或 null。
 */
export function validateAiAttachmentCount(existingCount: number, incomingCount: number): string | null {
  if (existingCount + incomingCount > AiAttachmentLimits.maxCount) {
    return `单条消息最多 ${AiAttachmentLimits.maxCount} 个附件。`;
  }
  return null;
}

/**
 * 把 File 读取为可提交的附件草稿（base64，不含 data: 前缀）；读取失败抛出原始异常由调用方提示。
 * @param file - 用户选择的文件。
 * @returns 附件草稿。
 */
export function readAiAttachment(file: File): Promise<AiAttachmentDraft> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    /** 读取失败回调：把底层错误翻译为带文件名的中文提示并拒绝。 */
    reader.onerror = () => reject(reader.error ?? new Error(`附件「${file.name}」读取失败。`));
    /** 读取成功回调：剥掉 data URL 前缀，只保留 base64 正文并组装附件草稿。 */
    reader.onload = () => {
      const result = String(reader.result ?? '');
      const comma = result.indexOf(',');
      const dataBase64 = comma >= 0 ? result.slice(comma + 1) : result;
      resolve({
        fileName: file.name,
        contentType: file.type || 'application/octet-stream',
        sizeBytes: file.size,
        dataBase64,
      });
    };
    reader.readAsDataURL(file);
  });
}

/**
 * 生成附件条的展示文本：文件名 + 人类可读大小（KB/MB 一位小数）。
 * @param attachment - 附件草稿。
 * @returns 展示文本（如 "费用.xlsx（12.3 KB）"）。
 */
export function aiAttachmentLabel(attachment: AiAttachmentDraft): string {
  const size = attachment.sizeBytes >= 1024 * 1024
    ? `${(attachment.sizeBytes / (1024 * 1024)).toFixed(1)} MB`
    : `${Math.max(1, Math.round(attachment.sizeBytes / 1024))} KB`;
  return `${attachment.fileName}（${size}）`;
}

/**
 * 把附件草稿集合转换为请求载荷形态；字段与后端 PocAiChatAttachment 一一对应。
 * @param attachments - 附件草稿集合。
 * @returns 可并入 AiChatRequest.attachments 的数组。
 */
export function toAiChatAttachments(attachments: readonly AiAttachmentDraft[]): AiChatAttachment[] {
  return attachments.map((attachment) => ({
    fileName: attachment.fileName,
    contentType: attachment.contentType,
    sizeBytes: attachment.sizeBytes,
    dataBase64: attachment.dataBase64,
  }));
}
