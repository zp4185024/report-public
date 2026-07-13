# 启动前准备

*   安装 docker、docker-compose 环境
*   使用 docker-compose -f docker-compose-builder.yml up  命令编译结果会直接被 docker-compose up使用  发布编译结果跟项目运行全部在linux docker环境 不再需要vs发布编译
*   docker-compose.yml 虽然配置了mysql 跟tdengine环境默认是没启用的，需要的话自行配置数据库链接

# 注意事项

1.  *docker/app/Configuration/Database.json* 文件不需要修改，不要覆盖掉了
2.  *Web/.env.production* 文件配置接口地址配置为 VITE\_API\_URL = '/prod-api'
3.  nginx、mysql 配置文件无需修改
4.  redis/redis.conf 中配置密码，如果不设密码 REDIS_PASSWORD 置空，app/Configuration/Cache.json中server=redis:6379，password 置空

***
# 编译命令
1. *docker-compose -f docker-compose-builder.yml up net-builder*    编译admin.net 项目
2. *docker-compose -f docker-compose-builder.yml up web-builder*    编译前端
3. *docker-compose -f docker-compose-builder.yml up down*           移除docker 容器方便下次编译


# 启动命令 

`docker-compose up -d`

# 访问入口

***<http://ip:9100>***
***<https://ip:9103>***
