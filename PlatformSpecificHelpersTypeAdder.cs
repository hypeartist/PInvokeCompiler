﻿namespace PInvokeCompiler
{
    using System.Collections.Generic;
    using System.Linq;
    using Microsoft.Cci;
    using Microsoft.Cci.MutableCodeModel;

    internal sealed class PlatformSpecificHelpersTypeAdder : MetadataRewriter
    {
        private readonly ITypeReference marshalClass;

        private readonly IMethodReference getOSVersion;

        private readonly IMethodReference getPlatform;

        public PlatformSpecificHelpersTypeAdder(IMetadataHost host, ITypeReference marshalClass, IMethodReference getOSVersion, IMethodReference getPlatform, bool copyAndRewriteImmutableReferences = false)
            : base(host, copyAndRewriteImmutableReferences)
        {
            this.marshalClass = marshalClass;
            this.getOSVersion = getOSVersion;
            this.getPlatform = getPlatform;
        }

        public override void RewriteChildren(Assembly module)
        {
            var typeDefList = new List<INamedTypeDefinition>();
            var rootUnitNamespace = new RootUnitNamespace();
            rootUnitNamespace.Members.AddRange(module.UnitNamespaceRoot.Members);
            module.UnitNamespaceRoot = rootUnitNamespace;
            rootUnitNamespace.Unit = module;

            var linux = CreatePlatformSpecificHelpers(this.host, rootUnitNamespace, "LinuxHelpers", "dlopen", "dlclose", "dlsym", "libdl", PInvokeCallingConvention.CDecl);
            var darwin = CreatePlatformSpecificHelpers(this.host, rootUnitNamespace, "DarwinHelpers", "dlopen", "dlclose", "dlsym", "libSystem", PInvokeCallingConvention.CDecl);
            var bsd = CreatePlatformSpecificHelpers(this.host, rootUnitNamespace, "BSDHelpers", "dlopen", "dlclose", "dlsym", "libc", PInvokeCallingConvention.CDecl);
            var windows = CreatePlatformSpecificHelpers(this.host, rootUnitNamespace, "WindowsHelpers", "LoadLibrary", "FreeLibrary", "GetProcAddress", "kernel32", PInvokeCallingConvention.StdCall, isUnix: false);
            var unix = CreateUnixHelpers(host, rootUnitNamespace, this.marshalClass, linux, darwin, bsd);
            var topLevel = CreatePInvokeHelpers(this.host, rootUnitNamespace, this.getOSVersion, this.getPlatform, windows, unix);

            typeDefList.Add(unix);
            typeDefList.Add(windows);
            typeDefList.Add(darwin);
            typeDefList.Add(linux);
            typeDefList.Add(bsd);
            typeDefList.Add(topLevel);


            foreach (var t in typeDefList)
            {
                rootUnitNamespace.Members.Add((INamespaceMember)t);
                module.AllTypes.Add(t);
            }
            
            base.RewriteChildren(module);
        }

        private static INamedTypeDefinition CreatePlatformSpecificHelpers(
            IMetadataHost host,
            IRootUnitNamespace rootUnitNamespace,
            string className,
            string loadLibraryMethodName,
            string freeLibraryMethodName,
            string getProcAddressMethodName,
            string moduleRef,
            PInvokeCallingConvention callingConvention,
            bool isUnix = true)
        {
            var typeDef = new NamespaceTypeDefinition();

            var loadLibrary = CreateLoadLibraryMethod(host, typeDef, moduleRef, loadLibraryMethodName, callingConvention);

            if (isUnix)
            {
                loadLibrary.Parameters.Add(new ParameterDefinition { Type = host.PlatformType.SystemInt32 });
            }

            var freeLibrary = CreateFreeLibraryMethod(host, typeDef, moduleRef, freeLibraryMethodName, callingConvention);
            var getProcAddress = CreateGetProcAddressMethod(host, typeDef, moduleRef, getProcAddressMethodName, callingConvention);

            typeDef.ContainingUnitNamespace = rootUnitNamespace;
            typeDef.Methods = new List<IMethodDefinition> { loadLibrary, freeLibrary, getProcAddress };
            typeDef.IsPublic = true;
            typeDef.BaseClasses = new List<ITypeReference> { host.PlatformType.SystemObject };
            typeDef.Name = host.NameTable.GetNameFor(className);

            return typeDef;
        }

        private static MethodDefinition CreateLoadLibraryMethod(IMetadataHost host, INamedTypeDefinition typeDef, string moduleRefName, string loadLibraryMethodName, PInvokeCallingConvention callingConvention)
        {
            return CreatePInvokeMethod(
                host,
                typeDef,
                new List<IParameterDefinition>
                {
                    new ParameterDefinition { Type = host.PlatformType.SystemString }
                },
                host.PlatformType.SystemIntPtr,
                moduleRefName,
                loadLibraryMethodName,
                callingConvention);
        }

        private static MethodDefinition CreateFreeLibraryMethod(IMetadataHost host, INamedTypeDefinition typeDef, string moduleRefName, string freeLibraryMethodName, PInvokeCallingConvention callingConvention)
        {
            return CreatePInvokeMethod(
                host,
                typeDef,
                new List<IParameterDefinition>
                {
                    new ParameterDefinition { Type = host.PlatformType.SystemIntPtr }
                },
                host.PlatformType.SystemInt32,
                moduleRefName,
                freeLibraryMethodName,
                callingConvention);
        }

        private static MethodDefinition CreateGetProcAddressMethod(IMetadataHost host, INamedTypeDefinition typeDef, string moduleRefName, string getProcAddressMethodName, PInvokeCallingConvention callingConvention)
        {
            return CreatePInvokeMethod(
                host,
                typeDef,
                new List<IParameterDefinition>
                {
                    new ParameterDefinition { Type = host.PlatformType.SystemIntPtr },
                    new ParameterDefinition { Type = host.PlatformType.SystemString }
                },
                host.PlatformType.SystemIntPtr,
                moduleRefName,
                getProcAddressMethodName,
                callingConvention);
        }

        private static MethodDefinition CreatePInvokeMethod(IMetadataHost host, INamedTypeDefinition typeDef, List<IParameterDefinition> parameters, ITypeReference returnType, string moduleRefName, string methodName, PInvokeCallingConvention callingConvention)
        {
            var exportMethodName = host.NameTable.GetNameFor(methodName);

            return new MethodDefinition
            {
                ContainingTypeDefinition = typeDef,
                Type = returnType,
                Parameters = parameters,
                Name = exportMethodName,
                Visibility = TypeMemberVisibility.Assembly,
                IsStatic = true,
                IsPlatformInvoke = true,
                IsHiddenBySignature = true,
                IsExternal = true,
                PlatformInvokeData = new PlatformInvokeInformation
                {
                    PInvokeCallingConvention = callingConvention,
                    ImportModule = new ModuleReference { ModuleIdentity = new ModuleIdentity(host.NameTable.GetNameFor(moduleRefName), "unknown://location") },
                    ImportName = exportMethodName
                }
            };
        }

        private static IMethodDefinition CreateGetOperatingSystemMethod(IMetadataHost host, INamedTypeDefinition typeDef, IMethodReference stringOPEquality, IMethodReference uname)
        {
            var methodDefinition = new MethodDefinition
            {
                Name = host.NameTable.GetNameFor("GetOperatingSystem"),
                IsStatic = true,
                ContainingTypeDefinition = typeDef,
                Visibility = TypeMemberVisibility.Assembly,
                Type = host.PlatformType.SystemInt32
            };

            ILGenerator ilGenerator = new ILGenerator(host, methodDefinition);
            ilGenerator.Emit(OperationCode.Call, uname);
            ilGenerator.Emit(OperationCode.Stloc_0);

            var linuxLabel = new ILGeneratorLabel();
            var darwinLabel = new ILGeneratorLabel();
            var freebsdLabel = new ILGeneratorLabel();
            var netbsdLabel = new ILGeneratorLabel();
            var unknownLabel = new ILGeneratorLabel();

            AddOperatingSystemCase(ilGenerator, "Linux", stringOPEquality, linuxLabel);
            AddOperatingSystemCase(ilGenerator, "Darwin", stringOPEquality, darwinLabel);
            AddOperatingSystemCase(ilGenerator, "FreeBSD", stringOPEquality, freebsdLabel);
            AddOperatingSystemCase(ilGenerator, "NetBSD", stringOPEquality, netbsdLabel);

            ilGenerator.Emit(OperationCode.Br, unknownLabel);

            ilGenerator.MarkLabel(linuxLabel);
            ilGenerator.Emit(OperationCode.Ldc_I4_1);
            ilGenerator.Emit(OperationCode.Ret);

            ilGenerator.MarkLabel(darwinLabel);
            ilGenerator.Emit(OperationCode.Ldc_I4_2);
            ilGenerator.Emit(OperationCode.Ret);

            ilGenerator.MarkLabel(freebsdLabel);
            ilGenerator.Emit(OperationCode.Ldc_I4_3);
            ilGenerator.Emit(OperationCode.Ret);

            ilGenerator.MarkLabel(netbsdLabel);
            ilGenerator.Emit(OperationCode.Ldc_I4_4);
            ilGenerator.Emit(OperationCode.Ret);

            ilGenerator.MarkLabel(unknownLabel);
            ilGenerator.Emit(OperationCode.Ldc_I4_0);
            ilGenerator.Emit(OperationCode.Ret);

            methodDefinition.Body = new ILGeneratorMethodBody(ilGenerator, true, 2, methodDefinition, new List<ILocalDefinition> { new LocalDefinition { Type = host.PlatformType.SystemString } }, new List<ITypeDefinition>());
            return methodDefinition;
        }

        private static void AddOperatingSystemCase(ILGenerator ilGenerator, string operatingSystemName, IMethodReference stringOPEquality, ILGeneratorLabel operatingSystemSwitchLabel)
        {
            ilGenerator.Emit(OperationCode.Ldloc_0);
            ilGenerator.Emit(OperationCode.Ldstr, operatingSystemName);
            ilGenerator.Emit(OperationCode.Call, stringOPEquality);
            ilGenerator.Emit(OperationCode.Brtrue, operatingSystemSwitchLabel);
        }

        private static IMethodDefinition CreateUnameMethod(IMetadataHost host, INamedTypeDefinition typeDef, IFieldReference intPtrZero, IMethodReference allocHGlobal, IMethodReference ptrToStringAnsi, IMethodReference freeHGlobal, IMethodReference unamePInvoke, IMethodReference intPtrOpInequality)
        {
            var methodDefinition = new MethodDefinition
            {
                Name = host.NameTable.GetNameFor("uname"),
                IsStatic = true,
                ContainingTypeDefinition = typeDef,
                Visibility = TypeMemberVisibility.Assembly,
                Type = host.PlatformType.SystemString
            };

            var ilGenerator = new ILGenerator(host, methodDefinition);
            ilGenerator.Emit(OperationCode.Ldsfld, intPtrZero);
            ilGenerator.Emit(OperationCode.Stloc_0);
            ilGenerator.BeginTryBody();
            ilGenerator.Emit(OperationCode.Ldc_I4, 0xff);
            ilGenerator.Emit(OperationCode.Call, allocHGlobal);
            ilGenerator.Emit(OperationCode.Stloc_0);
            ilGenerator.Emit(OperationCode.Ldloc_0);
            ilGenerator.Emit(OperationCode.Call, unamePInvoke);

            var emptyStringLabel = new ILGeneratorLabel();

            ilGenerator.Emit(OperationCode.Brtrue_S, emptyStringLabel);
            ilGenerator.Emit(OperationCode.Ldloc_0);

            ilGenerator.Emit(OperationCode.Call, ptrToStringAnsi);
            ilGenerator.Emit(OperationCode.Stloc_1);
            
            var exitLabel = new ILGeneratorLabel();
            var endFinallyLabel = new ILGeneratorLabel();

            ilGenerator.Emit(OperationCode.Leave_S, exitLabel);
            ilGenerator.Emit(OperationCode.Ldstr, "");
            ilGenerator.Emit(OperationCode.Stloc_1);
            ilGenerator.Emit(OperationCode.Leave_S, exitLabel);
            ilGenerator.EndTryBody();
            ilGenerator.BeginFinallyBlock();
            ilGenerator.Emit(OperationCode.Ldloc_0);
            ilGenerator.Emit(OperationCode.Ldsfld, intPtrZero);
            ilGenerator.Emit(OperationCode.Call, intPtrOpInequality);
            ilGenerator.Emit(OperationCode.Brfalse_S, endFinallyLabel);
            ilGenerator.Emit(OperationCode.Ldloc_0);
            ilGenerator.Emit(OperationCode.Call, freeHGlobal);
            ilGenerator.MarkLabel(endFinallyLabel);
            ilGenerator.Emit(OperationCode.Endfinally);
            ilGenerator.MarkLabel(exitLabel);
            ilGenerator.Emit(OperationCode.Ldloc_1);
            ilGenerator.Emit(OperationCode.Ret);

            methodDefinition.Body = new ILGeneratorMethodBody(ilGenerator, true, 2, methodDefinition, new List<ILocalDefinition> { new LocalDefinition { Type = host.PlatformType.SystemIntPtr }, new LocalDefinition { Type = host.PlatformType.SystemString } }, new List<ITypeDefinition>());
            return methodDefinition;
        }

        private static IMethodDefinition CreateUnamePInvokeMethod(IMetadataHost host, INamedTypeDefinition typeDef)
        {
            return CreatePInvokeMethod(host, typeDef, new List<IParameterDefinition> { new ParameterDefinition { Type = host.PlatformType.SystemIntPtr } }, host.PlatformType.SystemInt32, "libc", "uname", PInvokeCallingConvention.CDecl);
        }

        private static IMethodDefinition CreateDLOpen(IMetadataHost host, INamedTypeDefinition typeDef, IMethodReference getOperatingSystem, IMethodReference linuxHelpers, IMethodReference darwinHelpers, IMethodReference bsdHelpers)
        {
            return CreateDLMethod(host, typeDef, host.NameTable.GetNameFor("dlopen"), new List<IParameterDefinition> { new ParameterDefinition { Type = host.PlatformType.SystemString }, new ParameterDefinition { Type = host.PlatformType.SystemInt32 } }, host.PlatformType.SystemIntPtr, getOperatingSystem, linuxHelpers, darwinHelpers, bsdHelpers);
        }

        private static IMethodDefinition CreateDLClose(IMetadataHost host, INamedTypeDefinition typeDef, IMethodReference getOperatingSystem, IMethodReference linuxHelpers, IMethodReference darwinHelpers, IMethodReference bsdHelpers)
        {
            return CreateDLMethod(host, typeDef, host.NameTable.GetNameFor("dlclose"), new List<IParameterDefinition> { new ParameterDefinition { Type = host.PlatformType.SystemIntPtr }, new ParameterDefinition { Type = host.PlatformType.SystemInt32 } }, host.PlatformType.SystemInt32, getOperatingSystem, linuxHelpers, darwinHelpers, bsdHelpers);
        }

        private static IMethodDefinition CreateDLSym(IMetadataHost host, INamedTypeDefinition typeDef, IMethodReference getOperatingSystem, IMethodReference linuxHelpers, IMethodReference darwinHelpers, IMethodReference bsdHelpers)
        {
            return CreateDLMethod(host, typeDef, host.NameTable.GetNameFor("dlsym"), new List<IParameterDefinition> { new ParameterDefinition { Type = host.PlatformType.SystemIntPtr }, new ParameterDefinition { Type = host.PlatformType.SystemString } }, host.PlatformType.SystemIntPtr, getOperatingSystem, linuxHelpers, darwinHelpers, bsdHelpers);
        }

        private static IMethodDefinition CreateDLMethod(IMetadataHost host, INamedTypeDefinition typeDef, IName name, List<IParameterDefinition> parameters, ITypeReference returnType, IMethodReference getOperatingSystem, IMethodReference linuxHelpers, IMethodReference darwinHelpers, IMethodReference bsdHelpers)
        {
            var methodDefinition = new MethodDefinition
            {
                Name = name,
                Parameters = parameters,
                ContainingTypeDefinition = typeDef,
                IsStatic = true,
                Visibility = TypeMemberVisibility.Assembly,
                Type = returnType
            };

            var labels = new ILGeneratorLabel[4];

            var linuxLabel = new ILGeneratorLabel();
            var darwinLabel = new ILGeneratorLabel();
            var bsdLabel = new ILGeneratorLabel();
            var unknownLabel = new ILGeneratorLabel();

            labels[0] = linuxLabel;
            labels[1] = darwinLabel;
            labels[2] = bsdLabel;
            labels[3] = bsdLabel;

            var ilGenerator = new ILGenerator(host, methodDefinition);
            ilGenerator.Emit(OperationCode.Call, getOperatingSystem);
            ilGenerator.Emit(OperationCode.Stloc_0);
            ilGenerator.Emit(OperationCode.Ldloc_0);
            ilGenerator.Emit(OperationCode.Ldc_I4_1);
            ilGenerator.Emit(OperationCode.Sub);
            ilGenerator.Emit(OperationCode.Switch, labels);
            ilGenerator.Emit(OperationCode.Br_S, unknownLabel);

            AddOperatingSystemCase2(ilGenerator, linuxHelpers);
            AddOperatingSystemCase2(ilGenerator, darwinHelpers);
            AddOperatingSystemCase2(ilGenerator, bsdHelpers);

            ilGenerator.Emit(OperationCode.Ldstr, "Platform Not Supported");
            ilGenerator.Emit(OperationCode.Newobj, host.PlatformType.SystemException);
            ilGenerator.Emit(OperationCode.Throw);

            methodDefinition.Body = new ILGeneratorMethodBody(ilGenerator, true, 2, methodDefinition, new List<ILocalDefinition> { new LocalDefinition { Type = host.PlatformType.SystemInt32 } }, new List<ITypeDefinition>());
            return methodDefinition;
        }

        private static void AddOperatingSystemCase2(ILGenerator ilGenerator, IMethodReference helper)
        {
            ilGenerator.Emit(OperationCode.Ldarg_0);
            ilGenerator.Emit(OperationCode.Ldarg_1);
            ilGenerator.Emit(OperationCode.Call, helper);
            ilGenerator.Emit(OperationCode.Ret);
        }

        private static INamedTypeDefinition CreateUnixHelpers(IMetadataHost host, IRootUnitNamespace rootUnitNamespace, ITypeReference marshalClass, INamedTypeDefinition linux, INamedTypeDefinition darwin, INamedTypeDefinition bsd)
        {
            var typeDef = new NamespaceTypeDefinition();

            var intPtrZero = new FieldReference
            {
                Name = host.NameTable.GetNameFor("Zero"),
                ContainingType = host.PlatformType.SystemIntPtr,
                Type = host.PlatformType.SystemIntPtr
            };

            var allocalHGlobal = new Microsoft.Cci.MutableCodeModel.MethodReference
            {
                Name = host.NameTable.GetNameFor("AllocHGlobal"),
                ContainingType = marshalClass,
                Type = host.PlatformType.SystemIntPtr,
                Parameters = new List<IParameterTypeInformation> { new ParameterDefinition { Index = 0, Type = host.PlatformType.SystemInt32 } }
            };

            var ptrToStringAnsi = new Microsoft.Cci.MutableCodeModel.MethodReference
            {
                Name = host.NameTable.GetNameFor("PtrToStringAnsi"),
                ContainingType = marshalClass,
                Type = host.PlatformType.SystemString,
                Parameters = new List<IParameterTypeInformation> { new ParameterDefinition { Index = 0, Type = host.PlatformType.SystemIntPtr } }
            };

            var freeHGlobal = new Microsoft.Cci.MutableCodeModel.MethodReference
            {
                Name = host.NameTable.GetNameFor("FreeHGlobal"),
                ContainingType = marshalClass,
                Type = host.PlatformType.SystemVoid,
                Parameters = new List<IParameterTypeInformation> { new ParameterDefinition { Index = 0, Type = host.PlatformType.SystemIntPtr } }
            };

            var intPtrOpInEquality = new Microsoft.Cci.MutableCodeModel.MethodReference
            {
                Name = host.NameTable.OpInequality,
                ContainingType = host.PlatformType.SystemIntPtr,
                Type = host.PlatformType.SystemBoolean,
                Parameters = new List<IParameterTypeInformation> { new ParameterDefinition { Index = 0, Type = host.PlatformType.SystemIntPtr }, new ParameterDefinition { Index = 1, Type = host.PlatformType.SystemIntPtr } }
            };
            
            var unamePInvokeMethod = CreateUnamePInvokeMethod(host, typeDef);
            var unameMethod = CreateUnameMethod(host, typeDef, intPtrZero, allocalHGlobal, ptrToStringAnsi, freeHGlobal, unamePInvokeMethod, intPtrOpInEquality);

            var stringOpEquality = new Microsoft.Cci.MutableCodeModel.MethodReference
            {
                Name = host.NameTable.OpEquality,
                ContainingType = host.PlatformType.SystemString,
                Type = host.PlatformType.SystemBoolean,
                Parameters = new List<IParameterTypeInformation> { new ParameterDefinition { Index = 0, Type = host.PlatformType.SystemString }, new ParameterDefinition { Index = 1, Type = host.PlatformType.SystemString } }
            };

            var getosmethod = CreateGetOperatingSystemMethod(host, typeDef, stringOpEquality, unameMethod);

            var linuxMethodList = linux.Methods.ToList();
            var darwinMethodList = darwin.Methods.ToList();
            var bsdMethodList = bsd.Methods.ToList();

            var dlopen = CreateDLOpen(host, typeDef, getosmethod, linuxMethodList[0], darwinMethodList[0], bsdMethodList[0]);
            var dlclose = CreateDLClose(host, typeDef, getosmethod, linuxMethodList[1], darwinMethodList[1], bsdMethodList[1]);
            var dlsym = CreateDLSym(host, typeDef, getosmethod, linuxMethodList[2], darwinMethodList[2], bsdMethodList[2]);
            
            typeDef.ContainingUnitNamespace = rootUnitNamespace;
            typeDef.Methods = new List<IMethodDefinition> { unamePInvokeMethod, unameMethod, getosmethod, dlopen, dlclose, dlsym };
            typeDef.IsPublic = true;
            typeDef.BaseClasses = new List<ITypeReference> { host.PlatformType.SystemObject };
            typeDef.Name = host.NameTable.GetNameFor("UnixHelpers");

            return typeDef;
        }

        private static INamedTypeDefinition CreatePInvokeHelpers(IMetadataHost host, IRootUnitNamespace rootUnitNamespace, IMethodReference getOSVersion, IMethodReference getPlatform, INamedTypeDefinition windows, INamedTypeDefinition unix)
        {
            var typeDef = new NamespaceTypeDefinition
            {
                ContainingUnitNamespace = rootUnitNamespace,
                IsPublic = true,
                BaseClasses = new List<ITypeReference> { host.PlatformType.SystemObject },
                Name = host.NameTable.GetNameFor("PInvokeHelpers")
            };

            var isUnix = new FieldDefinition
            {
                IsStatic = true,
                IsReadOnly = true,
                Type = host.PlatformType.SystemBoolean
            };

            var windowsMethods = windows.Methods.ToList();
            var unixMethods = unix.Methods.ToList();

            var isUnixStaticFunction = CreateIsUnixStaticFunction(host, typeDef, getOSVersion, getPlatform);
            var cctor = CreateCCtor(host, typeDef, isUnix, isUnixStaticFunction);
            var loadlibrary = LoadLibrary(host, typeDef, windowsMethods[0], unixMethods[0], isUnix);
            var getprocaddress = GetProcAddress(host, typeDef, windowsMethods[1], unixMethods[1], isUnix);
            var freelibrary = FreeLibrary(host, typeDef, windowsMethods[2], unixMethods[2], isUnix);

            typeDef.Methods = new List<IMethodDefinition> { isUnixStaticFunction, cctor, loadlibrary, getprocaddress, freelibrary };
            return typeDef;
        }

        private static IMethodDefinition CreateCCtor(IMetadataHost host, INamedTypeDefinition typeDef, IFieldReference fieldRef, IMethodReference isUnixStaticFunction)
        {
            var methodDefinition = new MethodDefinition
            {
                ContainingTypeDefinition = typeDef,
                IsStatic = true,
                IsSpecialName = true,
                IsRuntimeSpecial = true,
                Visibility = TypeMemberVisibility.Assembly,
                Type = host.PlatformType.SystemVoid,
                IsHiddenBySignature = true,
                Name = host.NameTable.GetNameFor(".cctor")
            };

            var ilGenerator = new ILGenerator(host, methodDefinition);
            ilGenerator.Emit(OperationCode.Call, isUnixStaticFunction);
            ilGenerator.Emit(OperationCode.Stsfld, fieldRef);
            ilGenerator.Emit(OperationCode.Ret);

            methodDefinition.Body = new ILGeneratorMethodBody(ilGenerator, true, 8, methodDefinition, new List<ILocalDefinition>(), new List<ITypeDefinition>());

            return methodDefinition;
        }

        private static IMethodDefinition CreateIsUnixStaticFunction(IMetadataHost host, INamedTypeDefinition typeDef, IMethodReference getOSVersion, IMethodReference getPlatform)
        {
            var methodDefinition = new MethodDefinition
            {
                ContainingTypeDefinition = typeDef,
                IsStatic = true,
                Visibility = TypeMemberVisibility.Assembly,
                Type = host.PlatformType.SystemBoolean
            };

            var label = new ILGeneratorLabel();

            var ilGenerator = new ILGenerator(host, methodDefinition);
            ilGenerator.Emit(OperationCode.Call, getOSVersion);
            ilGenerator.Emit(OperationCode.Callvirt, getPlatform);
            ilGenerator.Emit(OperationCode.Ldc_I4_6);
            ilGenerator.Emit(OperationCode.Beq_S, label);
            ilGenerator.Emit(OperationCode.Call, getOSVersion);
            ilGenerator.Emit(OperationCode.Callvirt, getPlatform);
            ilGenerator.Emit(OperationCode.Ldc_I4_4);
            ilGenerator.Emit(OperationCode.Ceq);
            ilGenerator.Emit(OperationCode.Ret);
            ilGenerator.MarkLabel(label);
            ilGenerator.Emit(OperationCode.Ldc_I4_1);
            ilGenerator.Emit(OperationCode.Ret);

            methodDefinition.Body = new ILGeneratorMethodBody(ilGenerator, true, 8, methodDefinition, new List<ILocalDefinition>(), new List<ITypeDefinition>());

            return methodDefinition;
        }

        private static IMethodDefinition LoadLibrary(IMetadataHost host, INamedTypeDefinition typeDef, IMethodReference loadLibrary, IMethodReference dlopen, IFieldReference isUnix)
        {
            var methodDefinition = new MethodDefinition
            {
                Name = host.NameTable.GetNameFor("LoadLibrary"),
                ContainingTypeDefinition = typeDef,
                Parameters = new List<IParameterDefinition> { new ParameterDefinition { Type = host.PlatformType.SystemString } },
                IsStatic = true,
                Visibility = TypeMemberVisibility.Assembly,
                Type = host.PlatformType.SystemIntPtr
            };

            var ilGenerator = new ILGenerator(host, methodDefinition);

            var label = new ILGeneratorLabel();

            ilGenerator.Emit(OperationCode.Ldsfld, isUnix);
            ilGenerator.Emit(OperationCode.Brtrue_S, label);
            ilGenerator.Emit(OperationCode.Ldarg_0);
            ilGenerator.Emit(OperationCode.Call, loadLibrary);
            ilGenerator.Emit(OperationCode.Ret);
            ilGenerator.MarkLabel(label);
            ilGenerator.Emit(OperationCode.Ldarg_0);
            ilGenerator.Emit(OperationCode.Ldc_I4_1);
            ilGenerator.Emit(OperationCode.Call, dlopen);
            ilGenerator.Emit(OperationCode.Ret);

            methodDefinition.Body = new ILGeneratorMethodBody(ilGenerator, true, 8, methodDefinition, new List<ILocalDefinition>(), new List<ITypeDefinition>());

            return methodDefinition;
        }

        private static IMethodDefinition GetProcAddress(IMetadataHost host, INamedTypeDefinition typeDef, IMethodReference getProcAddress, IMethodReference dlsym, IFieldReference isUnix)
        {
            var methodDefinition = new MethodDefinition
            {
                Name = host.NameTable.GetNameFor("GetProcAddress"),
                ContainingTypeDefinition = typeDef,
                Parameters = new List<IParameterDefinition> { new ParameterDefinition { Type = host.PlatformType.SystemIntPtr }, new ParameterDefinition { Type = host.PlatformType.SystemString } },
                IsStatic = true,
                Visibility = TypeMemberVisibility.Assembly,
                Type = host.PlatformType.SystemIntPtr
            };
            
            var ilGenerator = new ILGenerator(host, methodDefinition);

            var label = new ILGeneratorLabel();

            ilGenerator.Emit(OperationCode.Ldsfld, isUnix);
            ilGenerator.Emit(OperationCode.Brtrue_S, label);
            ilGenerator.Emit(OperationCode.Ldarg_0);
            ilGenerator.Emit(OperationCode.Ldarg_1);
            ilGenerator.Emit(OperationCode.Call, getProcAddress);
            ilGenerator.Emit(OperationCode.Ret);
            ilGenerator.MarkLabel(label);
            ilGenerator.Emit(OperationCode.Ldarg_0);
            ilGenerator.Emit(OperationCode.Ldarg_1);
            ilGenerator.Emit(OperationCode.Call, dlsym);
            ilGenerator.Emit(OperationCode.Ret);

            methodDefinition.Body = new ILGeneratorMethodBody(ilGenerator, true, 8, methodDefinition, new List<ILocalDefinition>(), new List<ITypeDefinition>());

            return methodDefinition;
        }

        private static IMethodDefinition FreeLibrary(IMetadataHost host, INamedTypeDefinition typeDef, IMethodReference freeLibrary, IMethodReference dlclose, IFieldReference isUnix)
        {
            var methodDefinition = new MethodDefinition
            {
                Name = host.NameTable.GetNameFor("FreeLibrary"),
                ContainingTypeDefinition = typeDef,
                Parameters = new List<IParameterDefinition> { new ParameterDefinition { Type = host.PlatformType.SystemIntPtr } },
                IsStatic = true,
                Visibility = TypeMemberVisibility.Assembly,
                Type = host.PlatformType.SystemInt32
            };

            var ilGenerator = new ILGenerator(host, methodDefinition);
            var label = new ILGeneratorLabel();

            ilGenerator.Emit(OperationCode.Ldsfld, isUnix);
            ilGenerator.Emit(OperationCode.Brtrue_S, label);
            ilGenerator.Emit(OperationCode.Ldarg_0);
            ilGenerator.Emit(OperationCode.Call, freeLibrary);
            ilGenerator.Emit(OperationCode.Ret);
            ilGenerator.MarkLabel(label);
            ilGenerator.Emit(OperationCode.Ldarg_0);
            ilGenerator.Emit(OperationCode.Ldc_I4_0);
            ilGenerator.Emit(OperationCode.Call, dlclose);
            ilGenerator.Emit(OperationCode.Ret);

            methodDefinition.Body = new ILGeneratorMethodBody(ilGenerator, true, 8, methodDefinition, new List<ILocalDefinition>(), new List<ITypeDefinition>());

            return methodDefinition;
        }
    }
}