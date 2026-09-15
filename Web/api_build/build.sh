#!/bin/bash

dir="$(dirname "$0")"

moduleName="apiServices"
apiServicesPath="$dir/../src/api-services/"
apiUrl="http://localhost:5005/swagger/Default/swagger.json"

if [ -d "$apiServicesPath" ]; then
    echo "================================ 删除目录 $apiServicesPath ================================"
    rm -rf "$apiServicesPath"
fi

echo "================================ 开始生成 $moduleName ================================"

java -jar "$dir/swagger-codegen-cli.jar" generate -i "$apiUrl" -l typescript-axios -o "$apiServicesPath"

# 删除不必要的文件和文件夹
rm -rf "$apiServicesPath/.swagger-codegen"
rm -f "$apiServicesPath/.gitignore"
rm -f "$apiServicesPath/.npmignore"
rm -f "$apiServicesPath/.swagger-codegen-ignore"
rm -f "$apiServicesPath/git_push.sh"
rm -f "$apiServicesPath/package.json"
rm -f "$apiServicesPath/README.md"
rm -f "$apiServicesPath/tsconfig.json"

echo "================================ 生成结束 ================================"