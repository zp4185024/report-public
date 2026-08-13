import assert from 'node:assert/strict';
import test from 'node:test';

import { buildIframeUrl } from '../src/layout/routerView/iframeUrl.ts';

test('uses the shell LAN host for a localhost iframe', () => {
	assert.equal(
		buildIframeUrl('http://localhost:3000/report?a=1', 'abc', 'http://192.168.2.235:5176'),
		'http://192.168.2.235:3000/report?a=1&token=abc'
	);
});

test('uses the shell LAN host for a 127.0.0.1 iframe', () => {
	assert.equal(
		buildIframeUrl('http://127.0.0.1:3000/', 'abc', 'http://192.168.2.235:5176'),
		'http://192.168.2.235:3000/?token=abc'
	);
});

test('keeps localhost when the shell is also opened locally', () => {
	assert.equal(
		buildIframeUrl('http://localhost:3000/', 'abc', 'http://localhost:5176'),
		'http://localhost:3000/?token=abc'
	);
});

test('does not rewrite a non-loopback iframe host', () => {
	assert.equal(
		buildIframeUrl('https://example.com/report', 'abc', 'http://192.168.2.235:5176'),
		'https://example.com/report?token=abc'
	);
});

test('forwards dynamic route parameters but keeps the shell menu id out', () => {
	assert.equal(
		buildIframeUrl(
			'http://localhost:3000/report?existing=1',
			'abc',
			'http://192.168.2.235:5176',
			{ menuId: '665', departmentName: '方便门诊', tag: ['one', 'two'] },
		),
		'http://192.168.2.235:3000/report?existing=1&departmentName=%E6%96%B9%E4%BE%BF%E9%97%A8%E8%AF%8A&tag=one&tag=two&token=abc'
	);
});
