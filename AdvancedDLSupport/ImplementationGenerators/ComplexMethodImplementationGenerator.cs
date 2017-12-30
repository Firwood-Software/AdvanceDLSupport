﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using AdvancedDLSupport.Extensions;
using JetBrains.Annotations;

namespace AdvancedDLSupport.ImplementationGenerators
{
    /// <summary>
    /// Generates method implementations for methods involving complex types.
    /// </summary>
    internal class ComplexMethodImplementationGenerator : MethodImplementationGenerator
    {
        [NotNull]
        private readonly TypeTransformerRepository _transformerRepository;

        /// <summary>
        /// Initializes a new instance of the <see cref="ComplexMethodImplementationGenerator"/> class.
        /// </summary>
        /// <param name="targetModule">The module in which the method implementation should be generated.</param>
        /// <param name="targetType">The type in which the method implementation should be generated.</param>
        /// <param name="targetTypeConstructorIL">The IL generator for the target type's constructor.</param>
        /// <param name="configuration">The configuration object to use.</param>
        /// <param name="transformerRepository">The repository where type transformers are stored.</param>
        public ComplexMethodImplementationGenerator
        (
            [NotNull] ModuleBuilder targetModule,
            [NotNull] TypeBuilder targetType,
            [NotNull] ILGenerator targetTypeConstructorIL,
            ImplementationConfiguration configuration,
            [NotNull] TypeTransformerRepository transformerRepository
        )
            : base(targetModule, targetType, targetTypeConstructorIL, configuration)
        {
            _transformerRepository = transformerRepository;
        }

        /// <inheritdoc />
        protected override void GenerateImplementation(MethodInfo method, string symbolName, string uniqueMemberIdentifier)
        {
            var metadataAttribute = method.GetCustomAttribute<NativeSymbolAttribute>() ??
                                    new NativeSymbolAttribute(method.Name);

            var loweredMethod = GenerateLoweredMethod(method, uniqueMemberIdentifier);

            var delegateBuilder = GenerateDelegateType
            (
                loweredMethod.MethodInfo,
                uniqueMemberIdentifier,
                metadataAttribute.CallingConvention
            );

            var delegateBuilderType = delegateBuilder.CreateTypeInfo();
            var delegateField = GenerateDelegateField(uniqueMemberIdentifier, delegateBuilderType);

            GenerateDelegateInvokerBody(loweredMethod.Builder, loweredMethod.MethodInfo.ParameterTypes.ToArray(), delegateBuilderType, delegateField);
            GenerateComplexMethodBody(method, loweredMethod.Builder, loweredMethod.MethodInfo.ParameterTypes.ToList());

            AugmentHostingTypeConstructor(symbolName, delegateBuilderType, delegateField);
        }

        /// <summary>
        /// Generates the method body for the complex method implementation. This method will lower all required
        /// arguments and call the lowered method, then raise the return value if required.
        /// </summary>
        /// <param name="complexInterfaceMethod">The complex method definition.</param>
        /// <param name="loweredMethod">The lowered method definition.</param>
        /// <param name="loweredMethodParameterTypes">The parameter types of the lowered method definition.</param>
        private void GenerateComplexMethodBody
        (
            [NotNull] MethodInfo complexInterfaceMethod,
            [NotNull] MethodInfo loweredMethod,
            [NotNull, ItemNotNull] IReadOnlyList<Type> loweredMethodParameterTypes
        )
        {
            var methodBuilder = TargetType.DefineMethod
            (
                complexInterfaceMethod.Name,
                MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                CallingConventions.Standard,
                complexInterfaceMethod.ReturnType,
                complexInterfaceMethod.GetParameters().Select(p => p.ParameterType).ToArray()
            );

            var il = methodBuilder.GetILGenerator();

            if (Configuration.GenerateDisposalChecks)
            {
                EmitDisposalCheck(il);
            }

            var parameters = complexInterfaceMethod.GetParameters();
            var loweredParameterTypes = loweredMethodParameterTypes;

            // Emit lowered parameters
            il.Emit(OpCodes.Ldarg_0);
            for (var i = 1; i <= parameters.Length; ++i)
            {
                var parameter = parameters[i - 1];
                if (parameter.ParameterType.IsComplexType())
                {
                    var loweredParameterType = loweredParameterTypes[i - 1];
                    EmitParameterValueLowering(il, parameter.ParameterType, loweredParameterType, i);
                }
                else
                {
                    il.Emit(OpCodes.Ldarg, i);
                }
            }

            // Call lowered method
            il.Emit(OpCodes.Call, loweredMethod);

            // Emit return value raising
            if (complexInterfaceMethod.HasComplexReturnValue())
            {
                EmitValueRaising(il, complexInterfaceMethod.ReturnType, loweredMethod.ReturnType);
            }

            il.Emit(OpCodes.Ret);
        }

        /// <summary>
        /// Emits a set of IL instructions which will load the argument at the given index and lower it using
        /// its corresponding <see cref="ITypeTransformer"/>, then place that value back onto the stack.
        /// </summary>
        /// <param name="il">The generator where the IL is to be emitted.</param>
        /// <param name="complexType">The complex type that the parameter starts off as.</param>
        /// <param name="simpleType">The simple type that the parameter is to be lowered to.</param>
        /// <param name="argumentIndex">The index of the parameter.</param>
        private void EmitParameterValueLowering
        (
            [NotNull] ILGenerator il,
            [NotNull] Type complexType,
            [NotNull] Type simpleType,
            int argumentIndex
        )
        {
            var transformerType = typeof(ITypeTransformer<,>).MakeGenericType(complexType, simpleType);
            var lowerValueFunc = transformerType.GetMethod(nameof(ITypeTransformer<object, object>.LowerValue));

            EmitGetComplexTransformerCall(il, complexType);

            il.Emit(OpCodes.Ldarg, argumentIndex); // Load the complex argument
            EmitGetArgumentParameterInfoByIndex(il, argumentIndex); // Load the relevant parameter

            il.Emit(OpCodes.Callvirt, lowerValueFunc); // Lower it
        }

        /// <summary>
        /// Emits a set of IL instructions which will retrieve the current method, and get the argument specified by the
        /// given index, pushing it as a <see cref="ParameterInfo"/> onto the evaluation stack.
        /// </summary>
        /// <param name="il">The generator where the IL is to be emitted.</param>
        /// <param name="argumentIndex">The index of the argument to get.</param>
        private void EmitGetArgumentParameterInfoByIndex([NotNull] ILGenerator il, int argumentIndex)
        {
            if (argumentIndex == 0)
            {
                EmitGetMethodReturnParameter(il);
            }
            else
            {
                EmitGetParameterInfoByIndex(il, argumentIndex - 1);
            }
        }

        /// <summary>
        /// Emits a set of IL instructions which will retrieve the current method, get its parameters, and then get the
        /// parameter at the given index, pushing it onto the evaluation stack.
        /// </summary>
        /// <param name="il">The generator where the IL is to be emitted.</param>
        /// <param name="parameterIndex">The index of the parameter to get in the parameter array.</param>
        private void EmitGetParameterInfoByIndex([NotNull] ILGenerator il, int parameterIndex)
        {
            var getCurrentMethodFunc = typeof(MethodBase).GetMethod(nameof(MethodBase.GetCurrentMethod), BindingFlags.Public | BindingFlags.Static);
            var getParametersFunc = typeof(MethodBase).GetMethod(nameof(MethodBase.GetParameters), BindingFlags.Public | BindingFlags.Instance);

            il.Emit(OpCodes.Call, getCurrentMethodFunc);
            il.Emit(OpCodes.Callvirt, getParametersFunc);
            il.Emit(OpCodes.Ldc_I4, parameterIndex);
            il.Emit(OpCodes.Ldelem_Ref);
        }

        /// <summary>
        /// Emits a set of IL instructions which will retrieve the current method, get its return value parameter, and
        /// push it onto the evaluation stack.
        /// </summary>
        /// <param name="il">The generator where the IL is to be emitted.</param>
        private void EmitGetMethodReturnParameter([NotNull] ILGenerator il)
        {
            var getCurrentMethodFunc = typeof(MethodBase).GetMethod(nameof(MethodBase.GetCurrentMethod), BindingFlags.Public | BindingFlags.Static);
            var getReturnParamFunc = typeof(MethodInfo).GetProperty(nameof(MethodInfo.ReturnParameter), BindingFlags.Public | BindingFlags.Instance).GetMethod;

            il.Emit(OpCodes.Call, getCurrentMethodFunc);
            il.Emit(OpCodes.Callvirt, getReturnParamFunc);
        }

        /// <summary>
        /// Emits a set of IL instructions which will pop the topmost value from the evaluation stack, and raise its
        /// type using its corresponding <see cref="ITypeTransformer"/>, then place that value back onto the stack.
        /// </summary>
        /// <param name="il">The generator where the IL is to be emitted.</param>
        /// <param name="complexType">The complex type that the value should be raised to.</param>
        /// <param name="simpleType">The simple type that the value starts off as.</param>
        /// <param name="argumentIndex">
        /// The index of the argument that is being raised. Typically 0 for the return value.
        /// </param>
        private void EmitValueRaising
        (
            [NotNull] ILGenerator il,
            [NotNull] Type complexType,
            [NotNull] Type simpleType,
            int argumentIndex = 0
        )
        {
            var transformerType = typeof(ITypeTransformer<,>).MakeGenericType(complexType, simpleType);
            var raiseValueFunc = transformerType.GetMethod(nameof(ITypeTransformer<object, object>.RaiseValue));

            il.DeclareLocal(simpleType);
            il.Emit(OpCodes.Stloc_0); // Store the current value on the stack

            EmitGetComplexTransformerCall(il, complexType);

            il.Emit(OpCodes.Ldloc_0); // Load the result again
            EmitGetArgumentParameterInfoByIndex(il, argumentIndex);

            il.Emit(OpCodes.Callvirt, raiseValueFunc); // Raise it
        }

        /// <summary>
        /// Emits a set of IL instructions which will retrieve the <see cref="ITypeTransformer"/> registered for the
        /// given complex type, placing it onto the evaluating stack.
        /// </summary>
        /// <param name="il">The generator where the IL is to be emitted.</param>
        /// <param name="complexType">The complex type which the transformer should handle.</param>
        private void EmitGetComplexTransformerCall([NotNull] ILGenerator il, [NotNull] Type complexType)
        {
            var getTransformerFunc = typeof(TypeTransformerRepository).GetMethod(nameof(TypeTransformerRepository.GetComplexTransformer));
            var repoProperty = typeof(AnonymousImplementationBase).GetProperty
            (
                nameof(AnonymousImplementationBase.TransformerRepository),
                BindingFlags.Public | BindingFlags.Instance
            );

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, repoProperty.GetMethod); // Get the type transformer repository

            EmitTypeOf(il, complexType);
            il.Emit(OpCodes.Callvirt, getTransformerFunc); // Get the relevant type transformer
        }

        /// <summary>
        /// Emits a set of IL instructions which will produce the equivalent of a typeof(T) call, placing it onto the
        /// evaluation stack.
        /// </summary>
        /// <param name="il">The generator where the IL is to be emitted.</param>
        /// <param name="type">The type to be emitted.</param>
        private void EmitTypeOf([NotNull] ILGenerator il, [NotNull] Type type)
        {
            var getTypeFromHandleFunc = typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle));
            il.Emit(OpCodes.Ldtoken, type);
            il.Emit(OpCodes.Call, getTypeFromHandleFunc);
        }

        /// <summary>
        /// Generates a lowered method based on the given complex method, lowering its return value and parameters.
        /// </summary>
        /// <param name="complexInterfaceMethod">The complex interface method.</param>
        /// <param name="memberIdentifier">The unique member identifier to use.</param>
        /// <returns>(
        /// A <see cref="ValueTuple{T1, T2}"/>, containing the generated method builder and its paramete types.
        /// </returns>
        private (TransientMethodInfo MethodInfo, MethodBuilder Builder) GenerateLoweredMethod
        (
            [NotNull] MethodInfo complexInterfaceMethod,
            [NotNull] string memberIdentifier
        )
        {
            var newReturnType = LowerTypeIfRequired(complexInterfaceMethod.ReturnType);

            var newParameterTypes = new List<Type>();
            foreach (var parameter in complexInterfaceMethod.GetParameters())
            {
                newParameterTypes.Add(LowerTypeIfRequired(parameter.ParameterType));
            }

            var loweredMethodBuilder = TargetType.DefineMethod
            (
                $"{memberIdentifier}_lowered",
                MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                CallingConventions.Standard,
                newReturnType,
                newParameterTypes.ToArray()
            );

            loweredMethodBuilder.CopyCustomAttributesFrom(complexInterfaceMethod, newReturnType, newParameterTypes);

            var transientInfo = new TransientMethodInfo
            (
                loweredMethodBuilder,
                newParameterTypes,
                new List<CustomAttributeData>(complexInterfaceMethod.GetCustomAttributesData()),
                complexInterfaceMethod.ReturnParameter.Attributes,
                complexInterfaceMethod.GetParameters().Select(p => p.Name).ToList(),
                complexInterfaceMethod.GetParameters().Select(p => p.Attributes).ToList(),
                new List<CustomAttributeData>(complexInterfaceMethod.ReturnParameter.GetCustomAttributesData()),
                complexInterfaceMethod.GetParameters().Select(p => new List<CustomAttributeData>(p.GetCustomAttributesData())).ToList()
            );

            return (transientInfo, loweredMethodBuilder);
        }

        /// <summary>
        /// Lowers the provided type using its corresponding <see cref="ITypeTransformer"/>, if required.
        /// </summary>
        /// <param name="type">The type to lower.</param>
        /// <returns>The type, lowered by its transformer, or the original value.</returns>
        [NotNull]
        private Type LowerTypeIfRequired([NotNull] Type type)
        {
            if (type.IsComplexType())
            {
                var transformer = _transformerRepository.GetComplexTransformer(type);
                type = transformer.LowerType();
            }

            return type;
        }
    }
}
