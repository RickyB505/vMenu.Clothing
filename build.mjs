import { copyFileSync, mkdirSync, rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';

// vMenu identifies a plugin by its resource name, so renaming this renames its permissions and settings too.
const resourceName = 'vMenu.Clothing';

const root = dirname(fileURLToPath(import.meta.url));
const out = join(root, 'build', resourceName);

rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });

const shared = { bundle: true, format: 'iife', target: 'es2020', logLevel: 'warning' };

await build({ ...shared, entryPoints: [join(root, 'src/client/client.ts')], platform: 'browser', outfile: join(out, 'client.js') });
await build({ ...shared, entryPoints: [join(root, 'src/server/server.ts')], platform: 'node', outfile: join(out, 'server.js') });

copyFileSync(join(root, 'fxmanifest.lua'), join(out, 'fxmanifest.lua'));
copyFileSync(join(root, 'clothes.json'), join(out, 'clothes.json'));

console.log(`Resource ready: ${out}`);
