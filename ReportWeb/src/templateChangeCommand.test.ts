import { describe, expect, it } from 'vitest';
import { isTemplateChangeCommand } from './templateChangeCommand';

describe('isTemplateChangeCommand', () => {
  it('marks user commands as template changes', () => {
    expect(isTemplateChangeCommand({ type: 0 })).toBe(true);
  });

  it('ignores operations, mutations, and commands without a type', () => {
    expect(isTemplateChangeCommand({ type: 1 })).toBe(false);
    expect(isTemplateChangeCommand({ type: 2 })).toBe(false);
    expect(isTemplateChangeCommand({})).toBe(false);
  });
});
