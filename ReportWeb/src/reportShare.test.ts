import { describe, expect, it } from 'vitest';
import {
  createReportShareUrl,
  readReportQueryParameters,
  readReportShareParameters,
} from './reportShare';

describe('report sharing', () => {
  it('creates a dedicated share URL with only visible non-empty parameters', () => {
    const url = createReportShareUrl({
      reportCode: 'test 2',
      parameters: {
        hisOrganizeId: 'organ1001',
        PATIENT_SEX: 'F',
        empty: '',
        hiddenToken: 'secret',
      },
      visibleParameterCodes: ['hisOrganizeId', 'PATIENT_SEX', 'empty'],
      includeParameters: true,
      origin: 'https://reports.example.test',
    });

    expect(url).toBe(
      'https://reports.example.test/share/reports/test%202#p.hisOrganizeId=organ1001&p.PATIENT_SEX=F',
    );
  });

  it('preserves multi-select values and Chinese text through the fragment', () => {
    const url = createReportShareUrl({
      reportCode: 'test2',
      parameters: {
        departments: JSON.stringify(['急诊', '内科']),
      },
      visibleParameterCodes: ['departments'],
      includeParameters: true,
      origin: 'https://reports.example.test',
    });

    expect(readReportShareParameters(new URL(url).hash)).toEqual({
      departments: JSON.stringify(['急诊', '内科']),
    });
  });

  it('creates a parameter-free URL when carrying parameters is disabled', () => {
    expect(createReportShareUrl({
      reportCode: 'test2',
      parameters: { hisOrganizeId: 'organ1001' },
      visibleParameterCodes: ['hisOrganizeId'],
      includeParameters: false,
      origin: 'https://reports.example.test',
    })).toBe('https://reports.example.test/share/reports/test2');
  });

  it('ignores fragment entries outside the sharing parameter namespace', () => {
    expect(readReportShareParameters('#mode=edit&p.hisOrganizeId=organ1001')).toEqual({
      hisOrganizeId: 'organ1001',
    });
  });

  it('allows embedded callers to pass URL-prefill parameters in the query string', () => {
    expect(readReportQueryParameters(
      '?hospitalId=H01&deptId=D02&personId=P03&token=opaque',
    )).toEqual({
      hospitalId: 'H01',
      deptId: 'D02',
      personId: 'P03',
      token: 'opaque',
    });
  });
});
