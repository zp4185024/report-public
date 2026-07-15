interface UniverCommandInfo {
  type?: number;
}

const commandType = 0;

export function isTemplateChangeCommand(commandInfo: UniverCommandInfo): boolean {
  return commandInfo.type === commandType;
}
