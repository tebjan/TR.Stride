using Stride.Graphics;
using Stride.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TR.Stride.PostProcess
{
    public interface IAmbientOcclusionEffect
    {
        bool RequiresNormalBuffer { get; }

        void Draw(RenderDrawContext context, Texture color, Texture depth, Texture normals, out Texture output);
    }
}
