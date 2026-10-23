// Copies the license and the third-party notices from the repository root into the package before packing.
import { copyFileSync } from 'node:fs';

for (const file of ['LICENSE', 'THIRD-PARTY-NOTICES.md']) {
  copyFileSync(new URL(`../../${file}`, import.meta.url), new URL(`../${file}`, import.meta.url));
}
