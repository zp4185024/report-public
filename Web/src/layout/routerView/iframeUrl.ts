const localNetworkPermission = 'local-network-access';

/**
 * 校验 iframeUrl 的 is Loopback Hostname 条件，并返回可供后续流程判断的结果。
 * @param hostname - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function isLoopbackHostname(hostname: string): boolean {
	const normalizedHostname = hostname.toLowerCase().replace(/^\[|\]$/g, '');
	return normalizedHostname === 'localhost'
		|| normalizedHostname.endsWith('.localhost')
		|| normalizedHostname === '127.0.0.1'
		|| normalizedHostname === '::1';
}

/**
 * 校验 iframeUrl 的 is Ipv4 In Local Network 条件，并返回可供后续流程判断的结果。
 * @param hostname - 需要读取、校验或转换的业务值。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
function isIpv4InLocalNetwork(hostname: string): boolean {
	const parts = hostname.split('.').map(Number);
	if (parts.length !== 4 || parts.some((part) => !Number.isInteger(part) || part < 0 || part > 255)) {
		return false;
	}

	return parts[0] === 10
		|| parts[0] === 127
		|| (parts[0] === 169 && parts[1] === 254)
		|| (parts[0] === 172 && parts[1] >= 16 && parts[1] <= 31)
		|| (parts[0] === 192 && parts[1] === 168);
}

/**
 * 构建 iframeUrl 所需的 build Iframe Url 内容，并保证生成结果符合调用约定。
 * @param address - address 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param token - 调用受保护接口或校验请求身份所需的访问令牌。
 * @param baseOrigin - baseOrigin 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param query - query 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function buildIframeUrl(
	address: string,
	token: string,
	baseOrigin = window.location.origin,
	query: Readonly<Record<string, unknown>> = {},
): string {
	try {
		const url = new URL(address, baseOrigin);
		const baseUrl = new URL(baseOrigin);
		// A loopback hostname in a menu points at the computer running the browser.
		// When the shell is opened over the LAN, target the shell host instead.
		if (isLoopbackHostname(url.hostname) && !isLoopbackHostname(baseUrl.hostname)) {
			url.hostname = baseUrl.hostname;
		}
		// 菜单路由自身的标识不能污染业务页面；其余查询参数按原名传给 iframe。
		for (const [key, rawValue] of Object.entries(query)) {
			if (key === 'menuId' || rawValue === null || rawValue === undefined) continue;
			url.searchParams.delete(key);
			for (const value of Array.isArray(rawValue) ? rawValue : [rawValue]) {
				if (value !== null && value !== undefined && String(value) !== '') {
					url.searchParams.append(key, String(value));
				}
			}
		}
		url.searchParams.set('token', token);
		return url.toString();
	} catch {
		return address;
	}
}

/**
 * 查询或读取 iframeUrl 所需的 get Iframe Permissions 数据，并按调用约定返回结果。
 * @param address - address 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @param baseOrigin - baseOrigin 对应的业务输入；具体类型和空值范围以方法签名为准。
 * @returns 返回值与当前方法声明的类型及空值约束保持一致。
 */
export function getIframePermissions(address: string, baseOrigin = window.location.origin): string | undefined {
	try {
		const { hostname } = new URL(address, baseOrigin);
		const normalizedHostname = hostname.toLowerCase().replace(/^\[|\]$/g, '');
		const isLocalIpv6 = normalizedHostname.includes(':') && (
			normalizedHostname === '::1'
			|| normalizedHostname.startsWith('fc')
			|| normalizedHostname.startsWith('fd')
			|| normalizedHostname.startsWith('fe8')
			|| normalizedHostname.startsWith('fe9')
			|| normalizedHostname.startsWith('fea')
			|| normalizedHostname.startsWith('feb')
		);
		const isLocalNetwork = isLoopbackHostname(normalizedHostname)
			|| normalizedHostname.endsWith('.local')
			|| isLocalIpv6
			|| isIpv4InLocalNetwork(normalizedHostname);

		return isLocalNetwork ? localNetworkPermission : undefined;
	} catch {
		return undefined;
	}
}
