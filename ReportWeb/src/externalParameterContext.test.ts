import { describe, expect, it } from 'vitest';
import {
  externalParameterContext,
  externalParameterDestinations,
  externalParameterDocumentContext,
  externalParameterTemplateKeys,
  currentUrlParameterValues,
  parameterSourceCategory,
} from './externalParameterContext';

describe('externalParameterContext', () => {
  it('only forwards context keys declared by enabled external sources', () => {
    expect(externalParameterContext([
      {
        code: 'his',
        name: 'HIS',
        enabled: true,
        contextKeys: ['token'],
        fields: [],
      },
      {
        code: 'disabled',
        name: '停用接口',
        enabled: false,
        contextKeys: ['secret'],
        fields: [],
      },
    ], '?token=abc&secret=hidden&unused=value')).toEqual({ token: 'abc' });
  });

  it('derives whitelisted keys from configurable endpoint, body and headers', () => {
    const source = {
      code: 'other',
      name: '其它系统',
      baseUrl: 'https://example.test/api/',
      endpoint: 'users/{{userId}}',
      httpMethod: 'POST' as const,
      requestBodyTemplate: '{"token":"{{token}}"}',
      headersTemplate: '{"X-Organ":"{{organId}}"}',
      requestParameters: [],
      timeoutSeconds: 10,
      enabled: true,
      revision: 1,
      fields: [],
    };

    expect(externalParameterTemplateKeys(source)).toEqual(['organId', 'token', 'userId']);
    expect(externalParameterDocumentContext([source], '?token=t&organId=o&userId=u&unused=x'))
      .toEqual({ organId: 'o', token: 't', userId: 'u' });
  });

  it('derives URL keys from explicit dynamic request parameter mappings', () => {
    expect(externalParameterTemplateKeys({
      endpoint: 'profile',
      requestBodyTemplate: '{}',
      headersTemplate: '{}',
      requestParameters: [
        { name: 'content.token', urlKey: 'token', location: 'Body', required: false },
        { name: 'tenant', urlKey: 'organId', location: 'Query', required: true },
      ],
    })).toEqual(['organId', 'token']);
  });

  it('reads only configured dynamic values from the current URL', () => {
    expect(currentUrlParameterValues(
      ['token', 'organId'],
      '?token=abc&unused=hidden',
    )).toEqual({ token: 'abc' });
  });

  it('describes every configured destination without depending on parameter names', () => {
    const source = {
      endpoint: 'profile/{{routeValue}}',
      requestBodyTemplate: '{"content":{"fixed":"x","template":"{{bodyValue}}"}}',
      headersTemplate: '{"X-Template":"{{headerValue}}"}',
      requestParameters: [
        { name: 'content.dynamic', urlKey: 'bodyInput', location: 'Body' as const, required: false },
        { name: 'X-Dynamic', urlKey: 'headerInput', location: 'Header' as const, required: false },
        { name: 'page', urlKey: 'queryInput', location: 'Query' as const, required: false },
      ],
    };

    expect(externalParameterDestinations(source, 'bodyInput')).toEqual(['请求体：content.dynamic']);
    expect(externalParameterDestinations(source, 'headerInput')).toEqual(['请求头：X-Dynamic']);
    expect(externalParameterDestinations(source, 'queryInput')).toEqual(['查询参数：page']);
    expect(externalParameterDestinations(source, 'routeValue')).toEqual(['接口路径模板']);
    expect(externalParameterDestinations(source, 'bodyValue')).toEqual(['请求体 JSON 模板']);
    expect(externalParameterDestinations(source, 'headerValue')).toEqual(['请求头 JSON 模板']);
  });
});

describe('parameterSourceCategory', () => {
  it.each([
    ['Form', 'form'],
    ['Url', 'url'],
    ['LocalFilter', 'form'],
    ['System', 'system'],
    ['Fixed', 'system'],
    ['External', 'external'],
  ])('maps %s to %s', (sourceType, expected) => {
    expect(parameterSourceCategory(sourceType)).toBe(expected);
  });
});
