﻿using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using AdvancedDLSupport.Extensions;
using JetBrains.Annotations;

// ReSharper disable BitwiseOperatorOnEnumWithoutFlags
namespace AdvancedDLSupport.ImplementationGenerators
{
    /// <summary>
    /// Generates implementations for methods.
    /// </summary>
    internal class MethodImplementationGenerator : ImplementationGeneratorBase<MethodInfo>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MethodImplementationGenerator"/> class.
        /// </summary>
        /// <param name="targetModule">The module in which the method implementation should be generated.</param>
        /// <param name="targetType">The type in which the method implementation should be generated.</param>
        /// <param name="targetTypeConstructorIL">The IL generator for the target type's constructor.</param>
        /// <param name="configuration">The configuration object to use.</param>
        public MethodImplementationGenerator
        (
            [NotNull] ModuleBuilder targetModule,
            [NotNull] TypeBuilder targetType,
            [NotNull] ILGenerator targetTypeConstructorIL,
            ImplementationConfiguration configuration
        )
            : base(targetModule, targetType, targetTypeConstructorIL, configuration)
        {
        }

        /// <inheritdoc />
        protected override void GenerateImplementation(MethodInfo method, string symbolName, string uniqueMemberIdentifier)
        {
            var delegateBuilder = GenerateDelegateType(method, uniqueMemberIdentifier);

            // Create a delegate field
            var delegateBuilderType = delegateBuilder.CreateTypeInfo();
            var delegateField = GenerateDelegateField(uniqueMemberIdentifier, delegateBuilderType);

            GenerateDelegateInvoker(method, delegateBuilderType, delegateField);

            AugmentHostingTypeConstructor(symbolName, delegateBuilderType, delegateField);
        }

        /// <summary>
        /// Generats a field in the hosting type of the specified delegate type.
        /// </summary>
        /// <param name="uniqueMemberIdentifier">The unique member identifier to use.</param>
        /// <param name="delegateBuilderType">The type of delegate that the field should be.</param>
        /// <returns>The field.</returns>
        protected FieldBuilder GenerateDelegateField(string uniqueMemberIdentifier, TypeInfo delegateBuilderType)
        {
            FieldBuilder delegateField;
            if (Configuration.UseLazyBinding)
            {
                var lazyLoadedType = typeof(Lazy<>).MakeGenericType(delegateBuilderType);
                delegateField = TargetType.DefineField($"{uniqueMemberIdentifier}_dtm", lazyLoadedType, FieldAttributes.Public);
            }
            else
            {
                delegateField =
                    TargetType.DefineField($"{uniqueMemberIdentifier}_dtm", delegateBuilderType, FieldAttributes.Public);
            }

            return delegateField;
        }

        /// <summary>
        /// Augments the constructor of the hosting type with initialization logic for this method.
        /// </summary>
        /// <param name="entrypointName">The name of the native entry point.</param>
        /// <param name="delegateBuilderType">The type of the method delegate.</param>
        /// <param name="delegateField">The delegate field.</param>
        protected void AugmentHostingTypeConstructor
        (
            [NotNull] string entrypointName,
            [NotNull] Type delegateBuilderType,
            [NotNull] FieldInfo delegateField
        )
        {
            var loadFunc = typeof(AnonymousImplementationBase).GetMethod
            (
                "LoadFunction",
                BindingFlags.NonPublic | BindingFlags.Instance
            ).MakeGenericMethod(delegateBuilderType);

            TargetTypeConstructorIL.Emit(OpCodes.Ldarg_0); // This is for storing field delegate, it needs the "this" reference
            TargetTypeConstructorIL.Emit(OpCodes.Ldarg_0);

            if (Configuration.UseLazyBinding)
            {
                var lambdaBuilder = GenerateFunctionLoadingLambda(delegateBuilderType, entrypointName);
                GenerateLazyLoadedField(lambdaBuilder, delegateBuilderType);
            }
            else
            {
                TargetTypeConstructorIL.Emit(OpCodes.Ldstr, entrypointName);
                TargetTypeConstructorIL.EmitCall(OpCodes.Call, loadFunc, null);
            }

            TargetTypeConstructorIL.Emit(OpCodes.Stfld, delegateField);
        }

        /// <summary>
        /// Generates a method that invokes the method's delegate.
        /// </summary>
        /// <param name="method">The method to invoke.</param>
        /// <param name="delegateBuilderType">The type of the method delegate.</param>
        /// <param name="delegateField">The delegate field.</param>
        protected void GenerateDelegateInvoker
        (
            [NotNull] MethodInfo method,
            [NotNull] Type delegateBuilderType,
            [NotNull] FieldInfo delegateField
        )
        {
            var delegateInvoker = GenerateDelegateInvoker
            (
                new TransientMethodInfo(method),
                delegateBuilderType,
                delegateField
            );

            delegateInvoker.CopyCustomAttributesFrom(method, method.ReturnType, method.GetParameters().Select(p => p.ParameterType).ToList());
        }

        /// <summary>
        /// Generates a method that invokes the method's delegate.
        /// </summary>
        /// <param name="methodInfo">The method.</param>
        /// <param name="delegateBuilderType">The type of the method delegate.</param>
        /// <param name="delegateField">The delegate field.</param>
        /// <returns>The delegate invoker.</returns>
        [NotNull]
        protected MethodBuilder GenerateDelegateInvoker
        (
            [NotNull] TransientMethodInfo methodInfo,
            [NotNull] Type delegateBuilderType,
            [NotNull] FieldInfo delegateField
        )
        {
            var methodBuilder = TargetType.DefineMethod
            (
                methodInfo.Name,
                MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                CallingConventions.Standard,
                methodInfo.ReturnType,
                methodInfo.ParameterTypes.ToArray()
            );

            GenerateDelegateInvokerBody(methodBuilder, methodInfo.ParameterTypes.ToArray(), delegateBuilderType, delegateField);

            return methodBuilder;
        }

        /// <summary>
        /// Generates the method body for a delegate invoker.
        /// </summary>
        /// <param name="method">The method to generate the body for.</param>
        /// <param name="parameterTypes">The parameter types of the method.</param>
        /// <param name="delegateBuilderType">The type of the method delegate.</param>
        /// <param name="delegateField">The delegate field.</param>
        protected void GenerateDelegateInvokerBody
        (
            [NotNull] MethodBuilder method,
            [NotNull] Type[] parameterTypes,
            [NotNull] Type delegateBuilderType,
            [NotNull] FieldInfo delegateField
        )
        {
            // Let's create a method that simply invoke the delegate
            var methodIL = method.GetILGenerator();

            if (Configuration.GenerateDisposalChecks)
            {
                EmitDisposalCheck(methodIL);
            }

            GenerateSymbolPush(methodIL, delegateField);

            for (int p = 1; p <= parameterTypes.Length; p++)
            {
                methodIL.Emit(OpCodes.Ldarg, p);
            }

            methodIL.EmitCall(OpCodes.Call, delegateBuilderType.GetMethod("Invoke"), null);
            methodIL.Emit(OpCodes.Ret);
        }

        /// <summary>
        /// Generates a delegate type for the given method.
        /// </summary>
        /// <param name="method">The method.</param>
        /// <param name="memberIdentifier">The member identifier to use for name generation.</param>
        /// <returns>A delegate type.</returns>
        [NotNull]
        protected TypeBuilder GenerateDelegateType
        (
            [NotNull] MethodInfo method,
            [NotNull] string memberIdentifier
        )
        {
            var metadataAttribute = method.GetCustomAttribute<NativeSymbolAttribute>() ??
                                    new NativeSymbolAttribute(method.Name);

            return GenerateDelegateType
            (
                new TransientMethodInfo(method),
                memberIdentifier,
                metadataAttribute.CallingConvention
            );
        }

        /// <summary>
        /// Generates a delegate type for the given method.
        /// </summary>
        /// <param name="methodInfo">The method to generate a delegate type for.</param>
        /// <param name="memberIdentifier">The member identifier to use for name generation.</param>
        /// <param name="callingConvention">The unmanaged calling convention of the delegate.</param>
        /// <returns>A delegate type.</returns>
        [NotNull]
        protected TypeBuilder GenerateDelegateType
        (
            [NotNull] TransientMethodInfo methodInfo,
            [NotNull] string memberIdentifier,
            CallingConvention callingConvention
        )
        {
            // Declare a delegate type
            var delegateBuilder = TargetModule.DefineType
            (
                $"{memberIdentifier}_dt",
                TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.AnsiClass | TypeAttributes.AutoClass,
                typeof(MulticastDelegate)
            );

            var attributeConstructor = typeof(UnmanagedFunctionPointerAttribute).GetConstructors().First
            (
                c =>
                    c.GetParameters().Any() &&
                    c.GetParameters().Length == 1 &&
                    c.GetParameters().First().ParameterType == typeof(CallingConvention)
            );

            var functionPointerAttributeBuilder = new CustomAttributeBuilder
            (
                attributeConstructor,
                new object[] { callingConvention }
            );

            delegateBuilder.SetCustomAttribute(functionPointerAttributeBuilder);
            foreach (var attribute in methodInfo.CustomAttributes)
            {
                delegateBuilder.SetCustomAttribute(attribute.GetAttributeBuilder());
            }

            var delegateCtorBuilder = delegateBuilder.DefineConstructor
            (
                MethodAttributes.RTSpecialName | MethodAttributes.HideBySig | MethodAttributes.Public,
                CallingConventions.Standard,
                new[] { typeof(object), typeof(IntPtr) }
            );

            delegateCtorBuilder.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

            var delegateMethodBuilder = delegateBuilder.DefineMethod
            (
                "Invoke",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
                methodInfo.ReturnType,
                methodInfo.ParameterTypes.ToArray()
            );

            delegateMethodBuilder.CopyCustomAttributesFrom(methodInfo, methodInfo.ReturnType, methodInfo.ParameterTypes);

            delegateMethodBuilder.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
            return delegateBuilder;
        }
    }
}
