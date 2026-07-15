interface UniverCommandInfo {
  type?: number;
}

// Univer CommandType.COMMAND 的运行时值。OPERATION(1) 只改变选区/滚动，MUTATION(2) 也包含初始化公式回填。
const commandType = 0;

/**
 * 只把顶层用户命令视为模板修改。
 * 如果监听所有命令，工作簿初始化产生的 set-range-values mutation 会让刚加载的模板误显示“未保存”。
 */
export function isTemplateChangeCommand(commandInfo: UniverCommandInfo): boolean {
  return commandInfo.type === commandType;
}
