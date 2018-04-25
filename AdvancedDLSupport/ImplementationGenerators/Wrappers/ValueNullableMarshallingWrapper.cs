﻿//
//  ValueNullableMarshallingWrapper.cs
//
//  Copyright (c) 2018 Firwood Software
//
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using AdvancedDLSupport.Extensions;
using AdvancedDLSupport.Pipeline;
using AdvancedDLSupport.Reflection;
using JetBrains.Annotations;
using StrictEmit;

using static AdvancedDLSupport.ImplementationGenerators.GeneratorComplexity;

#pragma warning disable SA1513

namespace AdvancedDLSupport.ImplementationGenerators
{
    /// <summary>
    /// Generates wrapper instructions for marshalling string parameters, with an optional attribute-controlled
    /// cleanup step to free the marshalled memory afterwards.
    /// </summary>
    internal sealed class ValueNullableMarshallingWrapper : CallWrapperBase
    {
        /// <inheritdoc/>
        public override GeneratorComplexity Complexity => MemberDependent | TransformsParameters;

        /// <summary>
        /// Holds local variables defined for a given work unit. The nested dictionary contains the 0-based input
        /// parameter index matched with the local variable containing an unmanaged pointer.
        /// </summary>
        private Dictionary<PipelineWorkUnit<IntrospectiveMethodInfo>, Dictionary<int, LocalBuilder>> _workUnitLocals
            = new Dictionary<PipelineWorkUnit<IntrospectiveMethodInfo>, Dictionary<int, LocalBuilder>>();

        private static FieldInfo _nullPtrField;

        private static MethodInfo _ptrInequalityOperator;

        private static MethodInfo _structureToPtrMethod;
        private static MethodInfo _ptrToStructureMethodBase;

        private static MethodInfo _allocHGlobalMethod;
        private static MethodInfo _freeHGlobalMethod;

        static ValueNullableMarshallingWrapper()
        {
            _nullPtrField = typeof(IntPtr).GetField(nameof(IntPtr.Zero));

            _ptrInequalityOperator = typeof(IntPtr).GetMethod
            (
                "op_Inequality",
                new[] { typeof(IntPtr), typeof(IntPtr) }
            );

            _structureToPtrMethod = typeof(Marshal).GetMethod
            (
                nameof(Marshal.StructureToPtr),
                new[] { typeof(object), typeof(IntPtr), typeof(bool) }
            );

            _ptrToStructureMethodBase = typeof(Marshal).GetMethod
            (
                nameof(Marshal.PtrToStructure),
                new[] { typeof(IntPtr) }
            );

            _freeHGlobalMethod = typeof(Marshal).GetMethod
            (
                nameof(Marshal.FreeHGlobal),
                new[] { typeof(IntPtr) }
            );

            _allocHGlobalMethod = typeof(Marshal).GetMethod
            (
                nameof(Marshal.AllocHGlobal),
                new[] { typeof(int) }
            );
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ValueNullableMarshallingWrapper"/> class.
        /// </summary>
        /// <param name="targetModule">The module where the implementation should be generated.</param>
        /// <param name="targetType">The type in which the implementation should be generated.</param>
        /// <param name="targetTypeConstructorIL">The IL generator for the target type's constructor.</param>
        /// <param name="options">The configuration object to use.</param>
        public ValueNullableMarshallingWrapper
        (
            [NotNull] ModuleBuilder targetModule,
            [NotNull] TypeBuilder targetType,
            [NotNull] ILGenerator targetTypeConstructorIL,
            ImplementationOptions options
        )
            : base
            (
                targetModule,
                targetType,
                targetTypeConstructorIL,
                options
            )
        {
        }

        /// <inheritdoc />
        public override bool IsApplicable(IntrospectiveMethodInfo method)
        {
            var hasAnyByValueNullableParameters = method.ReturnType.IsNonRefNullable() ||
                                                  method.ParameterTypes.Any(p => p.IsNonRefNullable());

            return hasAnyByValueNullableParameters;
        }

        /// <inheritdoc />
        public override void EmitPrologue(ILGenerator il, PipelineWorkUnit<IntrospectiveMethodInfo> workUnit)
        {
            var definition = workUnit.Definition;

            var locals = new Dictionary<int, LocalBuilder>();
            _workUnitLocals.Add(workUnit, locals);

            // Load the "this" reference
            il.EmitLoadArgument(0);

            for (short i = 1; i <= definition.ParameterTypes.Count; ++i)
            {
                var parameterType = definition.ParameterTypes[i - 1];
                if (!parameterType.IsNonRefNullable())
                {
                    il.EmitLoadArgument(i);
                    continue;
                }

                var nullableType = parameterType.GetGenericArguments().First();
                var hasValueMethod = GetHasValueMethod(nullableType);
                var getValueMethod = GetGetValueMethod(nullableType);

                var trueCase = il.DefineLabel();
                var branchEnd = il.DefineLabel();

                var ptrLocal = il.DeclareLocal(typeof(IntPtr));

                il.EmitLoadArgumentAddress(i);
                il.EmitCallDirect(hasValueMethod);
                il.EmitBranchTrue(trueCase);

                // false case
                il.EmitLoadStaticField(_nullPtrField);
                il.EmitBranch(branchEnd);

                // true case
                il.MarkLabel(trueCase);

                il.EmitSizeOf(nullableType);
                il.EmitCallDirect(_allocHGlobalMethod);
                il.EmitSetLocalVariable(ptrLocal);

                il.EmitLoadArgumentAddress(i);
                il.EmitCallDirect(getValueMethod);
                il.EmitBox(nullableType);
                il.EmitLoadLocalVariable(ptrLocal);
                il.EmitConstantInt(0);

                il.EmitCallDirect(_structureToPtrMethod);

                il.EmitLoadLocalVariable(ptrLocal);

                il.MarkLabel(branchEnd);

                if (definition.ParameterHasCustomAttribute<CallerFreeAttribute>(i - 1))
                {
                    il.EmitSetLocalVariable(ptrLocal);
                    il.EmitLoadLocalVariable(ptrLocal);

                    locals.Add(i - 1, ptrLocal);
                }
            }
        }

        /// <inheritdoc />
        public override void EmitEpilogue(ILGenerator il, PipelineWorkUnit<IntrospectiveMethodInfo> workUnit)
        {
            var definition = workUnit.Definition;

            var locals = _workUnitLocals[workUnit];
            if (locals.Any())
            {
                // We have cleanup to do (freeing unmanaged structure memory)
                foreach (var localCombo in locals)
                {
                    var local = localCombo.Value;

                    var hasPointerLabel = il.DefineLabel();
                    var branchEnd = il.DefineLabel();

                    // Check if we have a pointer to clean up
                    il.EmitLoadLocalVariable(local);
                    il.EmitLoadStaticField(_nullPtrField);
                    il.EmitCallDirect(_ptrInequalityOperator);

                    il.EmitBranchTrue(hasPointerLabel);
                    // false case, null pointer
                    {
                        il.EmitBranch(branchEnd);
                    }
                    // true case, has pointer
                    {
                        il.MarkLabel(hasPointerLabel);
                        il.EmitLoadLocalVariable(local);
                        il.EmitCallDirect(_freeHGlobalMethod);
                    }
                    il.MarkLabel(branchEnd);
                }

                _workUnitLocals.Remove(workUnit);
            }

            if (!definition.ReturnType.IsNonRefNullable())
            {
                return;
            }

            var nullableType = definition.ReturnType.GetGenericArguments().First();
            var ptrToStructureMethod = _ptrToStructureMethodBase.MakeGenericMethod(nullableType);

            var returnIsNotNullLabel = il.DefineLabel();
            var returnBranchEndLabel = il.DefineLabel();

            var closedNullableType = typeof(Nullable<>).MakeGenericType(nullableType);
            var returnLocal = il.DeclareLocal(closedNullableType);

            var ptrLocal = il.DeclareLocal(typeof(IntPtr));

            // Store the pointer returned from native code
            il.EmitSetLocalVariable(ptrLocal);

            // Check if we have a valid value
            il.EmitLoadLocalVariable(ptrLocal);
            il.EmitLoadStaticField(_nullPtrField);
            il.EmitCallDirect(_ptrInequalityOperator);
            il.EmitBranchTrue(returnIsNotNullLabel);
            // false case, return null
            {
                il.EmitLoadLocalVariableAddress(returnLocal);
                il.EmitInitObject(closedNullableType);
                il.EmitLoadLocalVariable(returnLocal);
                il.EmitBranch(returnBranchEndLabel);
            }
            // true case, return marshalled structure
            il.MarkLabel(returnIsNotNullLabel);
            {
                // Marshal the structure from the pointer
                il.EmitLoadLocalVariable(ptrLocal);
                il.EmitCallDirect(ptrToStructureMethod);
                if (definition.ReturnParameterHasCustomAttribute<CallerFreeAttribute>())
                {
                    var marshalledReturnLocal = il.DeclareLocal(nullableType);

                    // And store it
                    il.EmitSetLocalVariable(marshalledReturnLocal);

                    // Free the pointer
                    il.EmitLoadLocalVariable(ptrLocal);
                    il.EmitCallDirect(_freeHGlobalMethod);

                    // Load the structure
                    il.EmitLoadLocalVariable(marshalledReturnLocal);
                }

                il.EmitNewObject(GetNullableConstructor(nullableType));
            }
            il.MarkLabel(returnBranchEndLabel);
        }

        /// <inheritdoc />
        public override IntrospectiveMethodInfo GeneratePassthroughDefinition(PipelineWorkUnit<IntrospectiveMethodInfo> workUnit)
        {
            var definition = workUnit.Definition;

            var newParameterTypes = definition.ParameterTypes.Select(t => t.IsNonRefNullable() ? typeof(IntPtr) : t).ToArray();
            var newReturnType = definition.ReturnType.IsNonRefNullable() ? typeof(IntPtr) : definition.ReturnType;

            var passthroughMethod = TargetType.DefineMethod
            (
                $"{workUnit.GetUniqueBaseMemberName()}_wrapped",
                MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                newReturnType,
                newParameterTypes
            );

            // Copy over all the attributes, except MarshalAsAttributes to IntPtr parameters
            passthroughMethod.ApplyCustomAttributesFrom
            (
                definition,
                newReturnType,
                newParameterTypes
            );

            return new IntrospectiveMethodInfo(passthroughMethod, newReturnType, newParameterTypes, definition);
        }

        [NotNull]
        private MethodInfo GetHasValueMethod(Type nullableType)
        {
            return typeof(Nullable<>)
                       .MakeGenericType(nullableType)
                       .GetProperty(nameof(Nullable<int>.HasValue))?.GetMethod
                   ?? throw new NullReferenceException();
        }

        [NotNull]
        private MethodInfo GetGetValueMethod(Type nullableType)
        {
            return typeof(Nullable<>)
                       .MakeGenericType(nullableType)
                       .GetProperty(nameof(Nullable<int>.Value))?.GetMethod
                   ?? throw new NullReferenceException();
        }

        [NotNull]
        private ConstructorInfo GetNullableConstructor(Type nullableType)
        {
            return typeof(Nullable<>).MakeGenericType(nullableType).GetConstructor
            (
                new[] { nullableType }
            )
            ?? throw new NullReferenceException();
        }
    }
}
