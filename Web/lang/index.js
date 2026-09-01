
    // 导入国际化JSON文件（合并模式）
    import langJSON from './index.json'
    (function () {
    // 定义翻译函数
    /**
     * 从指定命名空间读取翻译结果，缺失键时返回调用方提供的默认文案。
     * @param key - 需要查询的翻译键。
     * @param val - 未找到翻译时返回的默认文案。
     * @param nameSpace - 保存目标语言包的命名空间。
     * @returns 当前语言对应的翻译文案或默认文案。
     */
    let $tr = function (key, val, nameSpace) {
      // 获取指定命名空间下的语言包
      const langPackage = $tr[nameSpace];
      // 返回翻译结果，如果不存在则返回默认值
      return (langPackage || {})[key] || val;
    };
    // 定义简单翻译函数，直接返回传入的值
    /**
     * 原样返回无需翻译的文案，供自动扫描时显式跳过翻译处理。
     * @param val - 需要保持原文的文案。
     * @returns 未经修改的输入文案。
     */
    let $$tr = function (val) {
      return val;
    };
    /**
     * 原样返回深度扫描生成的模板文本，作为自动翻译插件的运行时兼容入口。
     * @param val - 自动扫描后需要继续传递的模板文本。
     * @returns 未经修改的模板文本。
     */
    globalThis.$deepScan = function (val) {
      return val;
    };
    /**
     * 使用位置参数替换文本中的占位符，参数无效或替换失败时保留原文。
     * @param val - 包含位置占位符的源文本。
     * @param args - 按占位符序号提供的替换值数组。
     * @returns 完成占位符替换后的文本。
     */
    globalThis.$iS = function (val, args) {
        // 如果参数不是字符串或数组，直接返回原值
        if (typeof val !== 'string' || !Array.isArray(args)) {
            return val;
        }
        try {
            // 使用更安全的正则表达式替换方式
            return val.replace(/\$(?:\{|\｛)(\d+)(?:\}|\｝)/g, (match, index) => {
                // 将index转换为数字
                const position = parseInt(index, 10);
                // 如果args[position]存在则替换，否则保留原占位符
                return args[position] !== undefined ? String(args[position]) : match;
            });
        } catch (error) {
            console.warn('字符串替换过程出现异常:', error);
            return val;
        }
    }
    // 定义设置语言包的方法
    /**
     * 把目标语言包注册到翻译函数的指定命名空间，空值按空语言包处理。
     * @param locale - 当前语言对应的键值集合。
     * @param nameSpace - 保存语言包的目标命名空间。
     */
    $tr.locale = function (locale, nameSpace) {
      // 将指定命名空间下的语言包设置为传入的locale
      $tr[nameSpace] = locale || {};
    };
    // 将翻译函数挂载到globalThis对象上，如果已经存在则使用已有的
    globalThis.$tr = globalThis.$tr || $tr;
    // 将简单翻译函数挂载到globalThis对象上
    globalThis.$$tr = $$tr;
    // 定义从JSON文件中获取指定键的语言对象的方法（合并模式）
    /**
     * 从合并语言配置中提取指定语言键，并构造成原文到译文的映射对象。
     * @param key - 需要提取的语言编码。
     * @param insertJSONObj - 包含全部语言条目的合并配置对象。
     * @returns 原文键到目标语言译文的映射。
     */
    globalThis._getJSONKey = function (key, insertJSONObj = undefined) {
        // 获取JSON对象
        const JSONObj = insertJSONObj;
        // 初始化语言对象
        const langObj = {};
        // 遍历JSON对象的所有键
        Object.keys(JSONObj).forEach((value) => {
            // 将每个语言的对应键值添加到语言对象中
            langObj[value] = JSONObj[value][key];
        });
        // 返回语言对象
        return langObj;
    };
    })();
    // 定义语言映射对象
    const langMap = {
        'en': (globalThis && globalThis.lang && globalThis.lang.en) ? globalThis.lang.en : globalThis._getJSONKey('en', langJSON),
'zhcn': (globalThis && globalThis.lang && globalThis.lang.zhcn) ? globalThis.lang.zhcn : globalThis._getJSONKey('zh-cn', langJSON)
    };
    globalThis.langMap = langMap;
    // 存储语言是否存在
    // 判断 globalThis.localStorage.getItem 是否为函数
    /**
     * 判断目标值是否可作为函数调用，避免直接访问不可用的存储接口。
     * @param fn - 需要检查的运行时值。
     * @returns 值为函数时返回 true，否则返回 false。
     */
    const isFunction = (fn) => {
        return typeof fn === 'function';
    };
    
    const withStorageLang = isFunction && globalThis && globalThis.localStorage && 
    isFunction(globalThis.localStorage.getItem) && globalThis.localStorage.getItem('lang');
    const withStorageCommonLang = isFunction && globalThis && globalThis.localStorage && 
    isFunction(globalThis.localStorage.getItem) && globalThis.localStorage.getItem('');
    // 从本地存储中获取通用语言，如果不存在则使用空字符串
    const commonLang = withStorageCommonLang ? globalThis.localStorage.getItem('') : '';
    // 从本地存储中获取当前语言，如果不存在则使用源语言
    const baseLang = withStorageLang ? globalThis.localStorage.getItem('lang') : 'zhcn';
    const lang = commonLang ? commonLang : baseLang;
    // 根据当前语言设置翻译函数的语言包
    globalThis.$tr.locale(globalThis.langMap[lang], 'lang');
    /**
     * 切换全局翻译函数当前使用的语言包。
     * @param lang - 目标语言在 langMap 中的编码。
     */
    globalThis.$changeLang = (lang) => {
        globalThis.$tr.locale(globalThis.langMap[lang], 'lang');
    };
