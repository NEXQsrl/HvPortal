import { describe, expect, it } from 'vitest';
import { dictionaryKeys, dictionaryParts } from './i18n';

describe('i18n', () => {
  it('Italian and English dictionaries have the same keys', () => {
    expect([...dictionaryKeys.it].sort()).toEqual([...dictionaryKeys.en].sort());
  });

  it('no key is defined in two dictionary files', () => {
    const owner = new Map<string, string>();
    const duplicates: string[] = [];
    for (const [part, dict] of Object.entries(dictionaryParts)) {
      for (const key of Object.keys(dict)) {
        const first = owner.get(key);
        if (first) duplicates.push(`${key} (${first}, ${part})`);
        else owner.set(key, part);
      }
    }
    expect(duplicates).toEqual([]);
  });
});
