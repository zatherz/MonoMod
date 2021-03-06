﻿using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MonoMod.NET40Shim;

namespace MonoMod {

    public delegate IMetadataTokenProvider Relinker(IMetadataTokenProvider mtp, IGenericParameterProvider context);
    public delegate void MethodRewriter(MethodDefinition method);
    public delegate void MethodBodyRewriter(MethodBody body, Instruction instr, int instri);
    public delegate ModuleDefinition MissingDependencyResolver(ModuleDefinition main, string name, string fullName);

    public static class MonoModExt {

        public static readonly Regex TypeGenericParamRegex = new Regex(@"\!\d");
        public static readonly Regex MethodGenericParamRegex = new Regex(@"\!\!\d");

        public static Type t_ParamArrayAttribute = typeof(ParamArrayAttribute);

        public static ModuleDefinition ReadModule(string path, ReaderParameters rp) {
            Retry:
            try {
                return ModuleDefinition.ReadModule(path, rp);
            } catch {
                if (rp.ReadSymbols) {
                    rp.ReadSymbols = false;
                    goto Retry;
                }
                throw;
            }
        }

        public static ModuleDefinition ReadModule(Stream input, ReaderParameters rp) {
            Retry:
            try {
                return ModuleDefinition.ReadModule(input, rp);
            } catch {
                if (rp.ReadSymbols) {
                    rp.ReadSymbols = false;
                    goto Retry;
                }
                throw;
            }
        }

        public static MethodBody Clone(this MethodBody o, MethodDefinition m) {
            if (o == null)
                return null;

            MethodBody c = new MethodBody(m);
            c.MaxStackSize = o.MaxStackSize;
            c.InitLocals = o.InitLocals;
            c.LocalVarToken = o.LocalVarToken;

            c.Instructions.AddRange(o.Instructions);
            c.ExceptionHandlers.AddRange(o.ExceptionHandlers);
            c.Variables.AddRange(o.Variables);

            m.CustomDebugInformations.AddRange(o.Method.CustomDebugInformations);
            m.DebugInformation.SequencePoints.AddRange(o.Method.DebugInformation.SequencePoints);

            return c;
        }

        public static bool IsHidden(this SequencePoint sp)
            =>  sp.StartLine == 0xFEEFEE &&
                sp.EndLine == 0xFEEFEE &&
                sp.StartColumn == 0 &&
                sp.EndColumn == 0;

        public readonly static System.Reflection.FieldInfo f_GenericParameter_position = typeof(GenericParameter).GetField("position", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        public readonly static System.Reflection.FieldInfo f_GenericParameter_type = typeof(GenericParameter).GetField("type", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        public static GenericParameter Update(this GenericParameter param, GenericParameter other)
            => param.Update(other.Position, other.Type);
        public static GenericParameter Update(this GenericParameter param, int position, GenericParameterType type) {
            f_GenericParameter_position.SetValue(param, position);
            f_GenericParameter_type.SetValue(param, type);
            return param;
        }

        public static void AddAttribute(this ICustomAttributeProvider cap, MethodReference constructor)
            => cap.AddAttribute(new CustomAttribute(constructor));
        public static void AddAttribute(this ICustomAttributeProvider cap, CustomAttribute attr)
            => cap.CustomAttributes.Add(attr);

        /// <summary>
        /// Determines if the attribute provider has got a specific MonoMod attribute.
        /// </summary>
        /// <returns><c>true</c> if the attribute provider contains the given MonoMod attribute, <c>false</c> otherwise.</returns>
        /// <param name="cap">Attribute provider to check.</param>
        /// <param name="attribute">Attribute.</param>
        public static bool HasMMAttribute(this ICustomAttributeProvider cap, string attribute)
            => cap.HasCustomAttribute("MonoMod.MonoMod" + attribute);
        public static CustomAttribute GetMMAttribute(this ICustomAttributeProvider cap, string attribute)
            => cap.GetCustomAttribute("MonoMod.MonoMod" + attribute);

        public static CustomAttribute GetCustomAttribute(this ICustomAttributeProvider cap, string attribute) {
            if (cap == null || !cap.HasCustomAttributes) return null;
            foreach (CustomAttribute attrib in cap.CustomAttributes)
                if (attrib.AttributeType.FullName == attribute)
                    return attrib;
            return null;
        }
        /// <summary>
        /// Determines if the attribute provider has got a specific custom attribute.
        /// </summary>
        /// <returns><c>true</c> if the attribute provider contains the given custom attribute, <c>false</c> otherwise.</returns>
        /// <param name="cap">Attribute provider to check.</param>
        /// <param name="attribute">Attribute.</param>
        public static bool HasCustomAttribute(this ICustomAttributeProvider cap, string attribute)
            => cap.GetCustomAttribute(attribute) != null;

        public static string GetOriginalName(this MethodDefinition method) {
            foreach (CustomAttribute attrib in method.CustomAttributes)
                if (attrib.AttributeType.FullName == "MonoMod.MonoModOriginalName")
                    return (string) attrib.ConstructorArguments[0].Value;

            return "orig_" + method.Name;
        }

        public static bool MatchingConditionals(this ICustomAttributeProvider cap) {
            if (cap == null) return true;
            if (!cap.HasCustomAttributes) return true;
            Platform plat = PlatformHelper.Current;
            bool status = true;
            foreach (CustomAttribute attrib in cap.CustomAttributes) {
                if (attrib.AttributeType.FullName == "MonoMod.MonoModOnPlatform") {
                    CustomAttributeArgument[] plats = (CustomAttributeArgument[]) attrib.ConstructorArguments[0].Value;
                    for (int i = 0; i < plats.Length; i++) {
                        if (PlatformHelper.Current.HasFlag((Platform) plats[i].Value)) {
                            // status &= true;
                            continue;
                        }
                    }
                    status &= plats.Length == 0;
                    continue;
                }

                if (attrib.AttributeType.FullName == "MonoMod.MonoModIfFlag") {
                    string flag = (string) attrib.ConstructorArguments[0].Value;
                    object valueObj;
                    bool value;
                    if (!MonoModder.Data.TryGetValue(flag, out valueObj) || !(valueObj is bool))
                        if (attrib.ConstructorArguments.Count == 2)
                            value = (bool) attrib.ConstructorArguments[1].Value;
                        else
                            value = true;
                    else
                        value = (bool) valueObj;
                    status &= value;
                }
            }
            return status;
        }

        public static string GetFindableID(this MethodReference method, string name = null, string type = null, bool withType = true, bool simple = false) {
            while (method.IsGenericInstance)
                method = ((GenericInstanceMethod) method).ElementMethod;

            StringBuilder builder = new StringBuilder();

            if (simple) {
                if (withType)
                    builder.Append(type ?? method.DeclaringType.FullName).Append("::");
                builder.Append(name ?? method.Name);
                return builder.ToString();
            }

            builder
                .Append(method.ReturnType.FullName)
                .Append(" ");

            if (withType)
                builder.Append(type ?? method.DeclaringType.FullName).Append("::");

            builder
                .Append(name ?? method.Name);

            if (method.GenericParameters.Count != 0) {
                builder.Append("<");
                Collection<GenericParameter> arguments = method.GenericParameters;
                for (int i = 0; i < arguments.Count; i++) {
                    if (i > 0)
                        builder.Append(",");
                    builder.Append(arguments[i].Name);
                }
                builder.Append(">");
            }

            builder.Append("(");

            if (method.HasParameters) {
                Collection<ParameterDefinition> parameters = method.Parameters;
                for (int i = 0; i < parameters.Count; i++) {
                    ParameterDefinition parameter = parameters[i];
                    if (i > 0)
                        builder.Append(",");

                    if (parameter.ParameterType.IsSentinel)
                        builder.Append("...,");

                    builder.Append(parameter.ParameterType.FullName);
                }
            }

            builder.Append(")");

            return builder.ToString();
        }

        public static string GetFindableID(this System.Reflection.MethodInfo method, string name = null, string type = null, bool withType = true, bool proxyMethod = false, bool simple = false) {
            while (method.IsGenericMethod)
                method = method.GetGenericMethodDefinition();

            StringBuilder builder = new StringBuilder();

            if (simple) {
                if (withType)
                    builder.Append(type ?? method.DeclaringType.FullName).Append("::");
                builder.Append(name ?? method.Name);
                return builder.ToString();
            }

            builder
                .Append(method.ReturnType.FullName)
                .Append(" ");

            if (withType)
                builder.Append(type ?? method.DeclaringType.FullName.Replace("+", "/")).Append("::");

            builder
                .Append(name ?? method.Name);

            if (method.ContainsGenericParameters) {
                builder.Append("<");
                Type[] arguments = method.GetGenericArguments();
                for (int i = 0; i < arguments.Length; i++) {
                    if (i > 0)
                        builder.Append(",");
                    builder.Append(arguments[i].Name);
                }
                builder.Append(">");
            }

            builder.Append("(");

            System.Reflection.ParameterInfo[] parameters = method.GetParameters();
            for (int i = proxyMethod ? 1 : 0; i < parameters.Length; i++) {
                System.Reflection.ParameterInfo parameter = parameters[i];
                if (i > (proxyMethod ? 1 : 0))
                    builder.Append(",");

                if (Attribute.IsDefined(parameter, t_ParamArrayAttribute))
                    builder.Append("...,");

                builder.Append(parameter.ParameterType.FullName);
            }

            builder.Append(")");

            return builder.ToString();
        }

        public static void UpdateOffsets(this MethodBody body, int instri, int delta) {
            for (int offsi = body.Instructions.Count - 1; instri <= offsi; offsi--)
                body.Instructions[offsi].Offset += delta;
        }

        public static int GetInt(this Instruction instr) {
            OpCode op = instr.OpCode;
            if (op == OpCodes.Ldc_I4_M1) return -1;
            if (op == OpCodes.Ldc_I4_0) return 0;
            if (op == OpCodes.Ldc_I4_1) return 1;
            if (op == OpCodes.Ldc_I4_2) return 2;
            if (op == OpCodes.Ldc_I4_3) return 3;
            if (op == OpCodes.Ldc_I4_4) return 4;
            if (op == OpCodes.Ldc_I4_5) return 5;
            if (op == OpCodes.Ldc_I4_6) return 6;
            if (op == OpCodes.Ldc_I4_7) return 7;
            if (op == OpCodes.Ldc_I4_8) return 8;
            if (op == OpCodes.Ldc_I4_S) return (sbyte) instr.Operand;
            return (int) instr.Operand;
        }

        public static bool ParseOnPlatform(this MethodDefinition method, ref int instri) {
            // Crawl back until we hit "newarr Platform" or "newarr int"
            int posNewarr = instri;
            for (; 1 <= posNewarr && method.Body.Instructions[posNewarr].OpCode != OpCodes.Newarr; posNewarr--) ;
            int pArrSize = method.Body.Instructions[posNewarr - 1].GetInt();
            bool matchingPlatformIL = pArrSize == 0;
            for (int ii = posNewarr + 1; ii < instri; ii += 4) {
                // dup
                // ldc.i4 INDEX
                Platform plat = (Platform) method.Body.Instructions[ii + 2].GetInt();
                // stelem.i4

                if (PlatformHelper.Current.HasFlag(plat)) {
                    matchingPlatformIL = true;
                    break;
                }
            }

            // If not matching platform, remove array code.
            if (!matchingPlatformIL) {
                for (int offsi = posNewarr - 1; offsi < instri; offsi++) {
                    method.Body.Instructions.RemoveAt(offsi);
                    instri = Math.Max(0, instri - 1);
                    method.Body.UpdateOffsets(instri, -1);
                    continue;
                }
            }
            return matchingPlatformIL;
        }

        public static void AddRange<T>(this Collection<T> list, Collection<T> other) {
            for (int i = 0; i < other.Count; i++)
                list.Add(other[i]);
        }

        public static void AddRange(this IDictionary dict, IDictionary other) {
            foreach (DictionaryEntry entry in other)
                dict.Add(entry.Key, entry.Value);
        }

        public static void PushRange<T>(this Stack<T> stack, T[] other) {
            foreach (T entry in other)
                stack.Push(entry);
        }
        public static void PopRange<T>(this Stack<T> stack, int n) {
            for (int i = 0; i < n; i++)
                stack.Pop();
        }

        public static void EnqueueRange<T>(this Queue<T> queue, T[] other) {
            foreach (T entry in other)
                queue.Enqueue(entry);
        }
        public static void DequeueRange<T>(this Queue<T> queue, int n) {
            for (int i = 0; i < n; i++)
                queue.Dequeue();
        }

        public static GenericParameter GetGenericParameter(this IGenericParameterProvider provider, string name) {
            // Don't ask me, that's possible for T[,].Get in "Enter the Gungeon"...?!
            if (provider is GenericParameter && ((GenericParameter) provider).Name == name)
                return (GenericParameter) provider;

            foreach (GenericParameter param in provider.GenericParameters)
                if (param.Name == name)
                    return param;

            int index;
            if (provider is MethodReference && MethodGenericParamRegex.IsMatch(name))
                if ((index = int.Parse(name.Substring(2))) < provider.GenericParameters.Count)
                    return provider.GenericParameters[index];
                else
                    return new GenericParameter(name, provider).Update(index, GenericParameterType.Method);

            if (provider is TypeReference && TypeGenericParamRegex.IsMatch(name))
                if ((index = int.Parse(name.Substring(1))) < provider.GenericParameters.Count)
                    return provider.GenericParameters[index];
                else
                    return new GenericParameter(name, provider).Update(index, GenericParameterType.Type);

            return
                (provider as TypeSpecification)?.ElementType.GetGenericParameter(name) ??
                (provider as MemberReference)?.DeclaringType?.GetGenericParameter(name);
        }

        public static IMetadataTokenProvider Relink(this IMetadataTokenProvider mtp, Relinker relinker, IGenericParameterProvider context) {
            if (mtp is TypeReference) return ((TypeReference) mtp).Relink(relinker, context);
            if (mtp is MethodReference) return ((MethodReference) mtp).Relink(relinker, context);
            if (mtp is FieldReference) return ((FieldReference) mtp).Relink(relinker, context);
            if (mtp is ParameterDefinition) return ((ParameterDefinition) mtp).Relink(relinker, context);
            throw new InvalidOperationException($"MonoMod can't handle metadata token providers of the type {mtp.GetType()}");
        }

        public static TypeReference Relink(this TypeReference type, Relinker relinker, IGenericParameterProvider context) {
            if (type == null)
                return null;

            if (type is TypeSpecification) {
                TypeSpecification ts = (TypeSpecification) type;
                TypeReference relinkedElem = ts.ElementType.Relink(relinker, context);

                if (type.IsByReference)
                    return new ByReferenceType(relinkedElem);

                if (type.IsPointer)
                    return new PointerType(relinkedElem);

                if (type.IsPinned)
					return new PinnedType(relinkedElem);

				if (type.IsArray)
                    return new ArrayType(relinkedElem, ((ArrayType) type).Dimensions.Count);

                if (type.IsRequiredModifier)
                    return new RequiredModifierType(((RequiredModifierType) type).ModifierType.Relink(relinker, context), relinkedElem);

                if (type.IsOptionalModifier)
                    return new OptionalModifierType(((OptionalModifierType) type).ModifierType.Relink(relinker, context), relinkedElem);

                if (type.IsGenericInstance) {
                    GenericInstanceType git = new GenericInstanceType(relinkedElem);
                    foreach (TypeReference genArg in ((GenericInstanceType) type).GenericArguments)
                        git.GenericArguments.Add(genArg?.Relink(relinker, context));
                    return git;
                }

                if (type.IsFunctionPointer) {
                    FunctionPointerType fp = (FunctionPointerType) type;
                    fp.ReturnType = fp.ReturnType.Relink(relinker, context);
                    for (int i = 0; i < fp.Parameters.Count; i++)
                        fp.Parameters[i].ParameterType = fp.Parameters[i].ParameterType.Relink(relinker, context);
                    return fp;
                }

				throw new InvalidOperationException($"MonoMod can't handle TypeSpecification: {type.FullName} ({type.GetType()})");
            }

            if (type.IsGenericParameter) {
                GenericParameter genParam = context.GetGenericParameter(((GenericParameter) type).Name);
                for (int i = 0; i < genParam.Constraints.Count; i++)
                    if (!genParam.Constraints[i].IsGenericInstance) // That is somehow possible and causes a stack overflow.
                        genParam.Constraints[i] = genParam.Constraints[i].Relink(relinker, context);
                return genParam;
            }

            return (TypeReference) relinker(type, context);
        }

        public static MethodReference Relink(this MethodReference method, Relinker relinker, IGenericParameterProvider context) {
            if (method.IsGenericInstance) {
                GenericInstanceMethod methodg = ((GenericInstanceMethod) method);
                GenericInstanceMethod gim = new GenericInstanceMethod(methodg.ElementMethod.Relink(relinker, context));
                foreach (TypeReference arg in methodg.GenericArguments)
                    // Generic arguments for the generic instance are often given by the next higher provider.
                    gim.GenericArguments.Add(arg.Relink(relinker, context));

                return (MethodReference) relinker(gim, context);
            }

            MethodReference relink = new MethodReference(method.Name, method.ReturnType, method.DeclaringType.Relink(relinker, context));

            relink.CallingConvention = method.CallingConvention;
            relink.ExplicitThis = method.ExplicitThis;
            relink.HasThis = method.HasThis;

            foreach (GenericParameter param in method.GenericParameters) {
                GenericParameter paramN = new GenericParameter(param.Name, param.Owner) {
                    Attributes = param.Attributes,
                    // MetadataToken = param.MetadataToken
                }.Update(param);

                relink.GenericParameters.Add(paramN);

                foreach (TypeReference constraint in param.Constraints) {
                    paramN.Constraints.Add(constraint.Relink(relinker, relink));
                }
            }

            relink.ReturnType = method.ReturnType?.Relink(relinker, relink);

            foreach (ParameterDefinition param in method.Parameters) {
                param.ParameterType = param.ParameterType.Relink(relinker, method);
                relink.Parameters.Add(param);
            }

            return (MethodReference) relinker(relink, context);
        }

        public static FieldReference Relink(this FieldReference field, Relinker relinker, IGenericParameterProvider context) {
            TypeReference declaringType = field.DeclaringType.Relink(relinker, context);
            return (FieldReference) relinker(new FieldReference(field.Name, field.FieldType.Relink(relinker, declaringType), declaringType), context);
        }

        public static ParameterDefinition Relink(this ParameterDefinition param, Relinker relinker, IGenericParameterProvider context) {
            param = ((MethodReference) param.Method).Relink(relinker, context).Parameters[param.Index];
            param.ParameterType = param.ParameterType.Relink(relinker, context);
            // Don't foreach when modifying the collection
            for (int i = 0; i < param.CustomAttributes.Count; i++)
                param.CustomAttributes[i] = param.CustomAttributes[i].Relink(relinker, context);
            return param;
        }

        public static ParameterDefinition Clone(this ParameterDefinition param) {
            ParameterDefinition newParam = new ParameterDefinition(param.Name, param.Attributes, param.ParameterType) {
                Constant = param.Constant,
                IsIn = param.IsIn,
                IsLcid = param.IsLcid,
                IsOptional = param.IsOptional,
                IsOut = param.IsOut,
                IsReturnValue = param.IsReturnValue
            };
            foreach (CustomAttribute attrib in param.CustomAttributes)
                newParam.CustomAttributes.Add(attrib.Clone());
            return newParam;
        }

        public static CustomAttribute Relink(this CustomAttribute attrib, Relinker relinker, IGenericParameterProvider context) {
            attrib.Constructor = attrib.Constructor.Relink(relinker, context);
            // Don't foreach when modifying the collection
            for (int i = 0; i < attrib.ConstructorArguments.Count; i++) {
                CustomAttributeArgument attribArg = attrib.ConstructorArguments[i];
                attrib.ConstructorArguments[i] = new CustomAttributeArgument(attribArg.Type.Relink(relinker, context), attribArg.Value);
            }
            for (int i = 0; i < attrib.Fields.Count; i++) {
                CustomAttributeNamedArgument attribArg = attrib.Fields[i];
                attrib.Fields[i] = new CustomAttributeNamedArgument(attribArg.Name,
                    new CustomAttributeArgument(attribArg.Argument.Type.Relink(relinker, context), attribArg.Argument.Value)
                );
            }
            for (int i = 0; i < attrib.Properties.Count; i++) {
                CustomAttributeNamedArgument attribArg = attrib.Properties[i];
                attrib.Properties[i] = new CustomAttributeNamedArgument(attribArg.Name,
                    new CustomAttributeArgument(attribArg.Argument.Type.Relink(relinker, context), attribArg.Argument.Value)
                );
            }
            return attrib;
        }

        public static CustomAttribute Clone(this CustomAttribute attrib) {
            CustomAttribute newAttrib = new CustomAttribute(attrib.Constructor, attrib.GetBlob());
            foreach (CustomAttributeArgument attribArg in attrib.ConstructorArguments)
                newAttrib.ConstructorArguments.Add(new CustomAttributeArgument(attribArg.Type, attribArg.Value));
            foreach (CustomAttributeNamedArgument attribArg in attrib.Fields)
                newAttrib.Fields.Add(new CustomAttributeNamedArgument(attribArg.Name,
                    new CustomAttributeArgument(attribArg.Argument.Type, attribArg.Argument.Value))
                );
            foreach (CustomAttributeNamedArgument attribArg in attrib.Properties)
                newAttrib.Properties.Add(new CustomAttributeNamedArgument(attribArg.Name,
                    new CustomAttributeArgument(attribArg.Argument.Type, attribArg.Argument.Value))
                );
            return newAttrib;
        }

        public static GenericParameter Relink(this GenericParameter param, Relinker relinker, IGenericParameterProvider context) {
            GenericParameter newParam = new GenericParameter(param.Name, param.Owner) {
                Attributes = param.Attributes
            }.Update(param);
            foreach (TypeReference constraint in param.Constraints)
                newParam.Constraints.Add(constraint.Relink(relinker, context));
            return newParam;
        }

        public static GenericParameter Clone(this GenericParameter param) {
            GenericParameter newParam = new GenericParameter(param.Name, param.Owner) {
                Attributes = param.Attributes
            }.Update(param);
            foreach (TypeReference constraint in param.Constraints)
                newParam.Constraints.Add(constraint);
            return newParam;
        }

        public static MethodDefinition FindMethod(this TypeDefinition type, string findableID) {
            // First pass: With type name (f.e. global searches)
            foreach (MethodDefinition method in type.Methods)
                if (method.GetFindableID() == findableID) return method;
            // Second pass: Without type name (f.e. LinkTo)
            foreach (MethodDefinition method in type.Methods)
                if (method.GetFindableID(withType: false) == findableID) return method;
            // Those shouldn't be reached, unless you're defining a relink map dynamically, which may conflict with itself.
            // First simple pass: With type name (just "Namespace.Type::MethodName")
            foreach (MethodDefinition method in type.Methods)
                if (method.GetFindableID(simple: true) == findableID) return method;
            // Second simple pass: Without type name (basically name only)
            foreach (MethodDefinition method in type.Methods)
                if (method.GetFindableID(withType: false, simple: true) == findableID) return method;
            return null;
        }
        public static PropertyDefinition FindProperty(this TypeDefinition type, string name) {
            foreach (PropertyDefinition prop in type.Properties)
                if (prop.Name == name) return prop;
            return null;
        }
        public static FieldDefinition FindField(this TypeDefinition type, string name) {
            foreach (FieldDefinition field in type.Fields)
                if (field.Name == name) return field;
            return null;
        }

        public static bool HasMethod(this TypeDefinition type, MethodDefinition method)
            => type.FindMethod(method.GetFindableID(withType: false)) != null;
        public static bool HasProperty(this TypeDefinition type, PropertyDefinition prop)
            => type.FindProperty(prop.Name) != null;
        public static bool HasField(this TypeDefinition type, FieldDefinition field)
            => type.FindField(field.Name) != null;

        public static void SetPublic(this IMetadataTokenProvider mtp, bool p) {
            if (mtp is TypeReference) ((TypeReference) mtp).Resolve()?.SetPublic(p);
            if (mtp is FieldReference) ((FieldReference) mtp).Resolve()?.SetPublic(p);
            if (mtp is MethodReference) ((MethodReference) mtp).Resolve()?.SetPublic(p);
            else if (mtp is TypeDefinition) ((TypeDefinition) mtp).SetPublic(p);
            else if (mtp is FieldDefinition) ((FieldDefinition) mtp).SetPublic(p);
            else if (mtp is MethodDefinition) ((MethodDefinition) mtp).SetPublic(p);
        }
        public static void SetPublic(this FieldDefinition o, bool p) {
            if (!o.IsDefinition || o.DeclaringType.Name == "<PrivateImplementationDetails>")
                return;
            o.IsPrivate = !p;
            o.IsPublic = p;
            if (p) o.DeclaringType.SetPublic(true);
        }
        public static void SetPublic(this MethodDefinition o, bool p) {
            if (!o.IsDefinition || o.DeclaringType.Name == "<PrivateImplementationDetails>")
                return;
            o.IsPrivate = !p;
            o.IsPublic = p;
            if (p) o.DeclaringType.SetPublic(true);
        }
        public static void SetPublic(this TypeDefinition o, bool p) {
            if (
                !o.IsDefinition ||
                o.Name == "<PrivateImplementationDetails>" ||
                (o.DeclaringType != null && o.DeclaringType.Name == "<PrivateImplementationDetails>")
            )
                return;
            if (o.DeclaringType == null) {
                o.IsNotPublic = !p;
                o.IsPublic = p;
            } else {
                o.IsNestedPrivate = !p;
                o.IsNestedPublic = p;
                if (p) SetPublic(o.DeclaringType, true);
            }
        }

        public static IMetadataTokenProvider ImportReference(this ModuleDefinition mod, IMetadataTokenProvider mtp) {
            if (mtp is TypeReference) return mod.ImportReference((TypeReference) mtp);
            if (mtp is FieldReference) return mod.ImportReference((FieldReference) mtp);
            if (mtp is MethodReference) return mod.ImportReference((MethodReference) mtp);
            return mtp;
        }

    }
}
