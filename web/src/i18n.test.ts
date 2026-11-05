import { describe, expect, it } from 'vitest';
import { dictionaryKeys } from './i18n';

describe('i18n', () => {
  it('Italian and English dictionaries have the same keys', () => {
    expect([...dictionaryKeys.it].sort()).toEqual([...dictionaryKeys.en].sort());
  });
});
