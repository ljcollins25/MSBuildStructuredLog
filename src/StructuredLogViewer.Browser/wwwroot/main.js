import { dotnet } from './_framework/dotnet.js';
import * as interop from './interop.js';

const runtime = await dotnet
    .withDiagnosticTracing(false)
    .create();

runtime.setModuleImports('interop.js', interop);

const config = runtime.getConfig();
await runtime.runMain(config.mainAssemblyName, []);
