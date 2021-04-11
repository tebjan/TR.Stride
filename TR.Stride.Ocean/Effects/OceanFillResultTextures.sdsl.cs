﻿// <auto-generated>
// Do not edit this file yourself!
//
// This code was generated by Stride Shader Mixin Code Generator.
// To generate it yourself, please install Stride.VisualStudio.Package .vsix
// and re-save the associated .sdfx.
// </auto-generated>

using System;
using Stride.Core;
using Stride.Rendering;
using Stride.Graphics;
using Stride.Shaders;
using Stride.Core.Mathematics;
using Buffer = Stride.Graphics.Buffer;

namespace TR.Stride.Ocean
{
    public static partial class OceanFillResultTexturesKeys
    {
        public static readonly ObjectParameterKey<Texture> Displacement = ParameterKeys.NewObject<Texture>();
        public static readonly ObjectParameterKey<Texture> Derivatives = ParameterKeys.NewObject<Texture>();
        public static readonly ObjectParameterKey<Texture> Turbulence = ParameterKeys.NewObject<Texture>();
        public static readonly ObjectParameterKey<Texture> Dx_Dz = ParameterKeys.NewObject<Texture>();
        public static readonly ObjectParameterKey<Texture> Dy_Dxz = ParameterKeys.NewObject<Texture>();
        public static readonly ObjectParameterKey<Texture> Dyx_Dyz = ParameterKeys.NewObject<Texture>();
        public static readonly ObjectParameterKey<Texture> Dxx_Dzz = ParameterKeys.NewObject<Texture>();
        public static readonly ValueParameterKey<float> Lambda = ParameterKeys.NewValue<float>();
        public static readonly ValueParameterKey<float> DeltaTime = ParameterKeys.NewValue<float>();
    }
}
