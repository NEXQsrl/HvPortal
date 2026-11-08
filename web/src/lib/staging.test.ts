import { describe, expect, it } from 'vitest';
import { driveOf, insideStorageFolders, suggestStagingPath, volumeOf } from './staging';

const GB = 1024 ** 3;
const vol = (name: string, freeGb: number) => ({ name, label: null, fileSystem: 'NTFS', sizeBytes: 500 * GB, freeBytes: freeGb * GB });

describe('backup staging folder', () => {
  it('suggests the local drive with the most free space, never a drive the host does not have', () => {
    expect(suggestStagingPath({ volumes: [vol('C:', 80)], storageFolders: [] })).toBe('C:\\HvPortal-staging');
    expect(suggestStagingPath({ volumes: [vol('C:', 80), vol('E:', 900)], storageFolders: null })).toBe('E:\\HvPortal-staging');
    expect(suggestStagingPath({ volumes: [], storageFolders: [] })).toBeNull();
  });

  it('stays inside the allowed storage folders of the host', () => {
    const host = { volumes: [vol('C:', 900), vol('D:', 100)], storageFolders: ['D:\\VMs', 'C:\\Hyper-V\\'] };
    expect(suggestStagingPath(host)).toBe('C:\\Hyper-V\\HvPortal-staging');
    expect(insideStorageFolders('D:\\VMs\\staging', ['D:\\VMs'])).toBe(true);
    expect(insideStorageFolders('D:\\VMs2\\staging', ['D:\\VMs'])).toBe(false);
    expect(insideStorageFolders('E:\\anything', [])).toBe(true);
  });

  it('finds the volume of a path', () => {
    expect(driveOf('c:\\HvPortal-staging')).toBe('C');
    expect(driveOf('\\\\nas\\share')).toBeNull();
    expect(volumeOf({ volumes: [vol('C:', 1)] }, 'C')?.name).toBe('C:');
    expect(volumeOf({ volumes: [vol('C:', 1)] }, 'D')).toBeUndefined();
  });
});
