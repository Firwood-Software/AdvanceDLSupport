﻿//
//  IntrospectiveMemberBase.cs
//
//  Copyright (c) 2018 Firwood Software
//
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JetBrains.Annotations;

namespace AdvancedDLSupport.Reflection
{
    /// <summary>
    /// Abstract base wrapper class for introspective member informations.
    /// </summary>
    /// <typeparam name="TMemberInfo">The member info to wrap.</typeparam>
    [PublicAPI]
    public abstract class IntrospectiveMemberBase<TMemberInfo> : MemberInfo, IIntrospectiveMember
        where TMemberInfo : MemberInfo
    {
        /// <inheritdoc cref="MemberInfo.Name" />
        [PublicAPI]
        public override string Name { get; }

        /// <summary>
        /// Gets the custom attributes applies to this member.
        /// </summary>
        [PublicAPI]
        public override IEnumerable<CustomAttributeData> CustomAttributes { get; }

        /// <inheritdoc />
        [PublicAPI]
        public override Type DeclaringType { get; }

        /// <inheritdoc />
        [PublicAPI]
        public override MemberTypes MemberType { get; }

        /// <inheritdoc />
        [PublicAPI]
        public override Type ReflectedType { get; }

        /// <summary>
        /// Gets the wrapped member.
        /// </summary>
        [PublicAPI]
        protected TMemberInfo Member { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="IntrospectiveMemberBase{TMemberInfo}"/> class.
        /// </summary>
        /// <param name="memberInfo">The member info object to wrap.</param>
        [PublicAPI]
        protected IntrospectiveMemberBase([NotNull] TMemberInfo memberInfo)
        {
            Member = memberInfo;

            Name = memberInfo.Name;
            DeclaringType = memberInfo.DeclaringType;
            MemberType = memberInfo.MemberType;
            ReflectedType = memberInfo.ReflectedType;

            CustomAttributes = memberInfo.CustomAttributes;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="IntrospectiveMemberBase{TMemberInfo}"/> class.
        /// </summary>
        /// <param name="memberInfo">The member info to wrap.</param>
        /// <param name="customAttributes">The custom attributes associated with the member.</param>
        [PublicAPI]
        protected IntrospectiveMemberBase
        (
            [NotNull] TMemberInfo memberInfo,
            [NotNull, ItemNotNull] IEnumerable<CustomAttributeData> customAttributes = default
        )
        {
            Member = memberInfo;
            Name = memberInfo.Name;
            DeclaringType = memberInfo.DeclaringType;
            MemberType = memberInfo.MemberType;
            ReflectedType = memberInfo.ReflectedType;

            CustomAttributes = customAttributes ?? new List<CustomAttributeData>();
        }

        /// <summary>
        /// Gets the wrapped member information. No guarantees can be made about its introspective capabilities.
        /// </summary>
        /// <returns>The wrapped method.</returns>
        [PublicAPI]
        public TMemberInfo GetWrappedMember() => Member;

        /// <inheritdoc />
        [PublicAPI]
        public override object[] GetCustomAttributes(bool inherit)
        {
            // TODO: Wrap properly
            return Member.GetCustomAttributes(inherit);
        }

        /// <inheritdoc />
        [PublicAPI]
        public override object[] GetCustomAttributes(Type attributeType, bool inherit)
        {
            // TODO: Wrap properly
            return Member.GetCustomAttributes(attributeType, inherit);
        }

        /// <inheritdoc />
        [PublicAPI]
        public override bool IsDefined(Type attributeType, bool inherit)
        {
            // TODO: Wrap properly
            return Member.IsDefined(attributeType, inherit);
        }

        /// <inheritdoc />
        [PublicAPI]
        public TAttribute GetCustomAttribute<TAttribute>() where TAttribute : Attribute
        {
            var matchingData = CustomAttributes.FirstOrDefault(a => a.AttributeType == typeof(TAttribute));

            if (matchingData is null)
            {
                return null;
            }

            var type = matchingData.AttributeType;
            var instance = Activator.CreateInstance(type, matchingData.ConstructorArguments.Select(a => a.Value).ToArray());

            foreach (var namedArgument in matchingData.NamedArguments)
            {
                if (namedArgument.MemberInfo is FieldInfo field)
                {
                    field.SetValue(instance, namedArgument.TypedValue.Value);
                }

                if (namedArgument.MemberInfo is PropertyInfo property)
                {
                    property.SetValue(instance, namedArgument.TypedValue.Value);
                }
            }

            return instance as TAttribute;
        }

        /// <summary>
        /// Explicitly casts to and accesses the wrapped member.
        /// </summary>
        /// <param name="introspectiveInfo">The introspective info.</param>
        /// <returns>The wrapped member.</returns>
        [PublicAPI]
        public static explicit operator TMemberInfo([CanBeNull] IntrospectiveMemberBase<TMemberInfo> introspectiveInfo)
        {
            return introspectiveInfo?.Member;
        }
    }
}
