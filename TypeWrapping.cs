using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace PoorlyTranslated
{
    public static class TypeWrapping
    {
        static AssemblyBuilder WrapperAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new("TypeWrappers"),
            AssemblyBuilderAccess.RunAndCollect
            );
        static ModuleBuilder WrapperModule = WrapperAssembly.DefineDynamicModule("RuntimeClassWrappersModule");

        static Dictionary<(Type type, Type @interface), Type> WrapperCache = new();

        public static T CreateWrapper<T>(object instance) where T : class
        {
            return (T)Activator.CreateInstance(CreateWrapperType(instance.GetType(), typeof(T)), new object[] { instance })!;
        }

        public static T CreateWrapper<T>(Type type, object instance) where T : class
        {
            return (T)Activator.CreateInstance(CreateWrapperType(type, typeof(T)), new object[] { instance })!;
        }

        public static Type CreateWrapperType(Type wrappingType, Type interfaceType)
        {
            if (WrapperCache.TryGetValue((wrappingType, interfaceType), out Type? cached))
                return cached;

            TypeBuilder wrapperType = WrapperModule.DefineType(wrappingType.FullName + ".Wrapper",
                TypeAttributes.Class | TypeAttributes.Public, null, new Type[] { interfaceType });

            FieldBuilder objectField = wrapperType.DefineField("WrappedObject", wrappingType, FieldAttributes.Private);

            ConstructorBuilder ctorBuilder = wrapperType.DefineConstructor(MethodAttributes.Public, CallingConventions.HasThis, new Type[] { wrappingType });
            ILGenerator ctor = ctorBuilder.GetILGenerator();

            ctor.Emit(OpCodes.Ldarg_0);
            ctor.Emit(OpCodes.Ldarg_1);
            ctor.Emit(OpCodes.Stfld, objectField);

            ConstructorBuilder cctorBuilder = wrapperType.DefineConstructor(MethodAttributes.Public | MethodAttributes.Static, CallingConventions.Standard, Array.Empty<Type>());
            ILGenerator cctor = cctorBuilder.GetILGenerator();

            HashSet<string> ignoreMethodNames = new();
            Dictionary<string, object> staticValues = new();

            foreach (PropertyInfo interfaceProperty in interfaceType.GetProperties())
            {
                PropertyBuilder property = wrapperType.DefineProperty(interfaceProperty.Name, PropertyAttributes.None, CallingConventions.HasThis, interfaceProperty.PropertyType, null);

                if (interfaceProperty.GetCustomAttribute<InstancePropertyAttribute>() is not null)
                {
                    BindInstanceProperty(interfaceProperty, property);
                    continue;
                }

                PropertyInfo? wrappingProperty = wrappingType.GetProperty(GetTargetName(interfaceProperty), (BindingFlags)(-1));

                if (wrappingProperty is not null)
                    BindProperty(interfaceProperty, property, wrappingProperty);
                else
                    BindField(interfaceProperty, property);
            }
            foreach (MethodInfo interfaceMethod in interfaceType.GetMethods())
            {
                if (ignoreMethodNames.Contains(interfaceMethod.Name))
                    continue;

                MethodBuilder method = wrapperType.DefineMethod(interfaceMethod.Name,
                                     MethodAttributes.Public | MethodAttributes.Virtual,
                                     interfaceMethod.ReturnType,
                                     interfaceMethod.GetParameters().Select(p => p.ParameterType).ToArray());

                BindMethod(interfaceMethod, method);
            }

            ctor.Emit(OpCodes.Ret);
            cctor.Emit(OpCodes.Ret);

            Type type = wrapperType.CreateType()!;

            foreach (var kvp in staticValues)
                type.GetField(kvp.Key, (BindingFlags)(-1))!.SetValue(null, kvp.Value);

            WrapperCache[(wrappingType, interfaceType)] = type;

            return type;

            void BindMethod(MethodInfo interfaceMethod, MethodBuilder wrapperMethod, MethodInfo? wrappingMethod = null, string? delegateName = null)
            {
                ParameterInfo[] parameters = interfaceMethod.GetParameters();
                Type[] args = parameters
                    .Where(p => p.GetCustomAttribute<InstanceParameterAttribute>() is null)
                    .Select(p => p.ParameterType)
                    .ToArray();

                int instanceParam = -1;
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].GetCustomAttribute<InstanceParameterAttribute>() is not null)
                    {
                        instanceParam = i + 1;
                        break;
                    }
                }

                string targetName = GetTargetName(interfaceMethod);

                wrappingMethod ??= wrappingType.GetMethods((BindingFlags)(-1)).FirstOrDefault(m =>
                {
                    return m.Name == targetName
                    && m.ReturnType == interfaceMethod.ReturnType
                    && m.GetParameters()
                        .Select(p => p.ParameterType)
                        .SequenceEqual(args);
                });

                if (wrappingMethod is null)
                    throw new InvalidDataException($"Cannot bind {interfaceMethod.DeclaringType!.Name}.{interfaceMethod.Name}({string.Join(", ", (object[])interfaceMethod.GetParameters())})");

                Type delegateType = CreateDelegateType(delegateName ?? CreateName(wrappingMethod, "Invoker"), wrappingMethod);

                FieldBuilder delegateField = wrapperType.DefineField(delegateType.Name, delegateType, FieldAttributes.Private | FieldAttributes.Static);

                ILGenerator il = wrapperMethod.GetILGenerator();

                staticValues[delegateField.Name] = wrappingMethod.CreateDelegate(delegateType);

                il.Emit(OpCodes.Ldsfld, delegateField);

                if (!wrappingMethod.IsStatic)
                {
                    if (instanceParam >= 0)
                    {
                        il.Emit(OpCodes.Ldarg, instanceParam);
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldfld, objectField);
                    }
                }

                int paramIndex = 0;
                if (!wrapperMethod.IsStatic)
                    paramIndex++;

                for (int i = 0; i < parameters.Length; i++)
                {
                    if (i != instanceParam - 1)
                        il.Emit(OpCodes.Ldarg, paramIndex);
                    paramIndex++;
                }

                il.Emit(OpCodes.Callvirt, delegateType.GetMethod("Invoke")!);
                il.Emit(OpCodes.Ret);
            }
            void BindField(PropertyInfo interfaceProperty, PropertyBuilder wrapperProperty)
            {
                string targetName = GetTargetName(interfaceProperty);

                FieldInfo? wrappingField = wrappingType.GetFields((BindingFlags)(-1)).FirstOrDefault(f => f.Name == targetName && f.FieldType == interfaceProperty.PropertyType);

                if (wrappingField is null)
                    throw new InvalidDataException($"Cannot bind {interfaceProperty.DeclaringType!.Name}.{interfaceProperty.Name}");

                if (interfaceProperty.GetMethod is not null)
                {
                    MethodBuilder getter = wrapperType.DefineMethod("get_" + interfaceProperty.Name,
                                     MethodAttributes.Public | MethodAttributes.Virtual,
                                     interfaceProperty.PropertyType, Array.Empty<Type>());

                    FieldInfo dynamicGetter = CreateFieldGetter(wrappingField);
                    ILGenerator il = getter.GetILGenerator();

                    il.Emit(OpCodes.Ldsfld, dynamicGetter);
                    if (!wrappingField.IsStatic)
                    {
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldfld, objectField);
                    }
                    il.Emit(OpCodes.Callvirt, dynamicGetter.FieldType.GetMethod("Invoke")!);
                    il.Emit(OpCodes.Ret);

                    wrapperProperty.SetGetMethod(getter);
                    ignoreMethodNames.Add(getter.Name);
                }

                if (interfaceProperty.SetMethod is not null)
                {
                    MethodBuilder setter = wrapperType.DefineMethod("set_" + interfaceProperty.Name,
                                     MethodAttributes.Public | MethodAttributes.Virtual,
                                     null, new Type[] { interfaceProperty.PropertyType });

                    FieldInfo dynamicSetter = CreateFieldSetter(wrappingField);
                    ILGenerator il = setter.GetILGenerator();

                    il.Emit(OpCodes.Ldsfld, dynamicSetter);
                    if (!wrappingField.IsStatic)
                    {
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldfld, objectField);
                    }
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Callvirt, dynamicSetter.FieldType.GetMethod("Invoke")!);
                    il.Emit(OpCodes.Ret);

                    wrapperProperty.SetSetMethod(setter);
                    ignoreMethodNames.Add(setter.Name);
                }
            }
            void BindProperty(PropertyInfo interfaceProperty, PropertyBuilder wrapperProperty, PropertyInfo wrappingProperty)
            {
                if (interfaceProperty.GetMethod is not null)
                {
                    if (wrappingProperty.GetMethod is null)
                        throw new InvalidDataException($"Cannot bind {interfaceProperty.DeclaringType!.Name}.{interfaceProperty.Name} getter");

                    MethodBuilder getter = wrapperType.DefineMethod("get_" + interfaceProperty.Name,
                                     MethodAttributes.Public | MethodAttributes.Virtual,
                                     interfaceProperty.PropertyType, Array.Empty<Type>());

                    BindMethod(interfaceProperty.GetMethod, getter, wrappingProperty.GetMethod, CreateName(wrappingProperty, "Getter", false));
                    wrapperProperty.SetGetMethod(getter);
                    ignoreMethodNames.Add(getter.Name);
                }

                if (interfaceProperty.SetMethod is not null)
                {
                    if (wrappingProperty.SetMethod is null)
                        throw new InvalidDataException($"Cannot bind {interfaceProperty.DeclaringType!.Name}.{interfaceProperty.Name} getter");

                    MethodBuilder setter = wrapperType.DefineMethod("set_" + interfaceProperty.Name,
                                     MethodAttributes.Public | MethodAttributes.Virtual,
                                     null, new Type[] { interfaceProperty.PropertyType });

                    BindMethod(interfaceProperty.SetMethod, setter, wrappingProperty.SetMethod, CreateName(wrappingProperty, "Setter", false));
                    wrapperProperty.SetSetMethod(setter);
                    ignoreMethodNames.Add(setter.Name);
                }
            }

            void BindInstanceProperty(PropertyInfo interfaceProperty, PropertyBuilder property)
            {
                if (interfaceProperty.GetMethod is not null)
                {
                    MethodBuilder getter = wrapperType.DefineMethod("get_" + interfaceProperty.Name,
                                                                    MethodAttributes.Public | MethodAttributes.Virtual,
                                                                    CallingConventions.HasThis,
                                                                    interfaceProperty.PropertyType,
                                                                    Array.Empty<Type>());
                    ILGenerator il = getter.GetILGenerator();

                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, objectField);
                    il.Emit(OpCodes.Ret);

                    property.SetGetMethod(getter);
                    ignoreMethodNames.Add(getter.Name);
                }
                if (interfaceProperty.SetMethod is not null)
                {
                    MethodBuilder setter = wrapperType.DefineMethod("set_" + interfaceProperty.Name,
                                                                    MethodAttributes.Public | MethodAttributes.Virtual,
                                                                    CallingConventions.HasThis,
                                                                    null,
                                                                    new Type[] { interfaceProperty.PropertyType });
                    ILGenerator il = setter.GetILGenerator();

                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Stfld, objectField);
                    il.Emit(OpCodes.Ret);

                    property.SetGetMethod(setter);
                    ignoreMethodNames.Add(setter.Name);
                }
            }

            FieldInfo CreateFieldGetter(FieldInfo field)
            {
                Type[] paramTypes = field.IsStatic ? Array.Empty<Type>() : new Type[] { field.DeclaringType! };
                DynamicMethod dm = new(CreateName(field, "Getter", true), field.FieldType, paramTypes, WrapperModule, true);

                ILGenerator il = dm.GetILGenerator();

                if (field.IsStatic)
                {
                    il.Emit(OpCodes.Ldsfld, field);
                }
                else
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, field);
                }
                il.Emit(OpCodes.Ret);

                Type delegateType = CreateDelegateType(CreateName(field, "Getter"), dm, paramTypes);

                FieldBuilder delegateField = wrapperType.DefineField(delegateType.Name, delegateType, FieldAttributes.Private | FieldAttributes.Static);

                staticValues[delegateField.Name] = dm.CreateDelegate(delegateType);

                return delegateField;
            }
            FieldInfo CreateFieldSetter(FieldInfo field)
            {
                Type[] paramTypes = field.IsStatic ? new Type[] { field.FieldType } : new Type[] { field.DeclaringType!, field.FieldType };

                DynamicMethod dm = new(CreateName(field, "Setter", true), null, paramTypes, WrapperModule, true);

                ILGenerator il = dm.GetILGenerator();

                if (field.IsStatic)
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Stsfld, field);
                }
                else
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Stfld, field);
                }
                il.Emit(OpCodes.Ret);

                Type delegateType = CreateDelegateType(CreateName(field, "Setter"), dm, paramTypes);

                FieldBuilder delegateField = wrapperType.DefineField(delegateType.Name, delegateType, FieldAttributes.Private | FieldAttributes.Static);

                staticValues[delegateField.Name] = dm.CreateDelegate(delegateType);

                return delegateField;
            }

            string GetTargetName(MemberInfo member)
            {
                TargetMemberAttribute? target = member.GetCustomAttribute<TargetMemberAttribute>();
                return target?.Name ?? member.Name;
            }
        }

        // https://stackoverflow.com/a/9507589/13645088
        static Type CreateDelegateType(string name, MethodInfo method, IEnumerable<Type>? paramTypes = null!)
        {
            var typeBuilder = WrapperModule.DefineType(
                name, TypeAttributes.Sealed | TypeAttributes.Public, typeof(MulticastDelegate));

            var constructor = typeBuilder.DefineConstructor(
                MethodAttributes.RTSpecialName | MethodAttributes.HideBySig | MethodAttributes.Public,
                CallingConventions.Standard, new[] { typeof(object), typeof(IntPtr) });
            constructor.SetImplementationFlags(MethodImplAttributes.CodeTypeMask);

            if (paramTypes is null)
                paramTypes = method.GetParameters().Select(p => p.ParameterType);

            if (!method.IsStatic)
                paramTypes = paramTypes.Prepend(method.DeclaringType)!;

            var invokeMethod = typeBuilder.DefineMethod(
                "Invoke", MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.Public,
                method.ReturnType, paramTypes.ToArray());
            invokeMethod.SetImplementationFlags(MethodImplAttributes.CodeTypeMask);

            return typeBuilder.CreateType()!;
        }

        static string CreateName(MemberInfo member, string type, bool skipType = false) =>
            skipType ? $"{member.Name}{type}" : $"{member.DeclaringType?.FullName ?? "Unknown"}.{member.Name}{type}";
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Method | AttributeTargets.Field)]
    public class TargetMemberAttribute : Attribute
    {
        public string Name { get; }

        public TargetMemberAttribute(string name)
        {
            Name = name;
        }
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public class InstanceParameterAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Property)]
    public class InstancePropertyAttribute : Attribute { }
}
