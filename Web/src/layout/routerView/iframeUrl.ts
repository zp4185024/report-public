const localNetworkPermission = 'local-network-access';

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

export function buildIframeUrl(address: string, token: string, baseOrigin = window.location.origin): string {
	try {
		const url = new URL(address, baseOrigin);
		url.searchParams.set('token', token);
		return url.toString();
	} catch {
		return address;
	}
}

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
		const isLocalNetwork = normalizedHostname === 'localhost'
			|| normalizedHostname.endsWith('.localhost')
			|| normalizedHostname.endsWith('.local')
			|| isLocalIpv6
			|| isIpv4InLocalNetwork(normalizedHostname);

		return isLocalNetwork ? localNetworkPermission : undefined;
	} catch {
		return undefined;
	}
}
