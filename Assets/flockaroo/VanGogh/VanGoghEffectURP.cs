//#define USE_URP
#if USE_URP
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.Universal;
using System;
using Random=UnityEngine.Random;

namespace Flockaroo
{

public class VanGoghEffectURP : ScriptableRendererFeature {

    [System.Serializable]
    public class EffectSettings
    {
        public RenderPassEvent WhenToApply = RenderPassEvent.BeforeRenderingPostProcessing;

        [Header("Input/Output")]

        [Tooltip("take a texture as input instead of the camera")]
        public Texture inputTexture;
        [Tooltip("render to a texture instead of the screen")]
        public bool renderToTexture = false;
        [Tooltip("texture being rendered to if above is checked")]
        public RenderTexture outputTexture;
        [Tooltip("generate mipmap for output texture")]
        public bool outputMipmap = false;

        [Header("Effect")]

        [Range(0.0f, 1.0f)]
        public float MasterFade = 1.0f;
        [Range(0.0f, 1.0f)]
        public float diffuseStrength = 0.200000f;
        [Range(0.0f, 5.0f)]
        public float smearStrength = 0.700000f;
        [Range(0.0f, 1.0f)]
        public float specularStrength = 0.600000f;
        [Range(0.0f, 6.4f)]
        public float strokeAngle = 0.100000f;
        [Range(0.0f, 32.0f)]
        public float NumSamples = 24.000000f;
        [Range(0.0f, 1.0f)]
        public float vignette = 1.000000f;
        //###PublicVars
        [Header("Other")]
        public bool flipY=false;
        public bool geomFlipY=false;

        [Tooltip("check this if you use linear color space in HDRP")]
        public bool HDRPGamma = true;

    }//class EffectSettings

    public EffectSettings settings = new EffectSettings();

    class EffectRenderPass: ScriptableRenderPass
    {
        string profilerTag;
        EffectSettings s;
        public RenderTargetIdentifier cameraColorTargetIdent;
        RenderTargetHandle tempTexture;
        RenderTargetHandle tempTexture2;
        RenderTexture tmpRT1;
        RenderTexture tmpRT2;
    List <string> bufferOrder = new List <string>();
    Dictionary<string, RenderTexture> buffers  = new Dictionary<string, RenderTexture>();
    Dictionary<string, Material>     shaders  = new Dictionary<string, Material>();
    Dictionary<string, Texture>      textures = new Dictionary<string, Texture>();
    Dictionary<string, Dictionary <int,string>> textureCh = new Dictionary<string, Dictionary <int,string>>();
    Dictionary<string, Dictionary <int,bool>> textureDemandsMip = new Dictionary<string, Dictionary <int,bool>>();
    Dictionary<string, List<Mesh>>   meshes   = new Dictionary<string, List<Mesh>>();
    Dictionary<string, int>   meshNumTri   = new Dictionary<string, int>();
    Dictionary<string, int>   defineMode      = new Dictionary<string, int>();
    RenderTexture mainTex = null;
    RenderTexture mainMip = null;
    private RenderTexture mySrc = null;
    private RenderTexture mySrc2 = null;
    private RenderTexture myInputTex = null;
    Regex refRegex = new Regex(@"Ref:([^:]+):Tex([0-9]+)");
    private int actWidth=0;
    private int actHeight=0;
    private Material gammaShader = null;
    private List<Mesh> screenQuadMesh = null;
    //private int NumTriangles=0;

    //FIXME: automate useMipOnMain - activate when needed
    private bool useMipOnMain = false;
    private bool DoHDRPGamma = true; // we set this to 'false' if we take care of gamma in the effect already

    public EffectRenderPass(string tag, ref EffectSettings settings, RenderPassEvent renderPassEvent)
    {
        this.profilerTag=tag;
        this.s=settings;
        this.renderPassEvent=renderPassEvent;
    }

    Material createShader(string resname)
    {
        Shader shader = Resources.Load<Shader>(resname);
        if(shader==null) return null;
        Material mat = new Material(shader);
        mat.hideFlags = HideFlags.HideAndDontSave;
        return mat;
    }

    Material createShaderOptSuff(string resname, List <string> suff)
    {
        Material mat = null;
        if(suff.Count>0)
        {
            if (mat==null) mat = createShaderOptSuff(resname+suff[0],suff.GetRange(1,suff.Count-1));
            if (mat==null) mat = createShaderOptSuff(resname,suff.GetRange(1,suff.Count-1));
        }
        if (mat==null) mat = createShader(resname);
        return mat;
    }

    RenderTexture createRenderTex(int w = -1, int h = -1, bool mip = false, int aa = 1)
    {
        RenderTexture rt;
        //if(w==-1) w=Screen.width;
        //if(h==-1) h=Screen.height;
        if(w==-1) w=actWidth;
        if(h==-1) h=actHeight;
        rt = new RenderTexture(w, h,0,RenderTextureFormat.ARGBFloat);
        rt.antiAliasing=aa; // must be 1 for mipmapping to work!!
        rt.useMipMap=mip;
        if(mip)
        rt.filterMode=FilterMode.Trilinear;
        return rt;
    }

    Texture2D createRandTex(int w, int h)
    {
        //if (RandTex == null)
        //    RandTex = Resources.Load<Texture2D>("rand256");
        Texture2D RandTex;
        {
            RandTex = new Texture2D(w, h, TextureFormat.RGBAFloat, true);
            //RandTex = new Texture2D(w, h, TextureFormat.RGBAHalf, true);
            //RandTex = new Texture2D(w, h, TextureFormat.RGBA32, true);

            for (int x = 0; x < RandTex.width; x++)
            {
                for (int y = 0; y < RandTex.height; y++)
                {
                    float r = Random.Range(0.0f, 1.0f);
                    float g = Random.Range(0.0f, 1.0f);
                    float b = Random.Range(0.0f, 1.0f);
                    float a = Random.Range(0.0f, 1.0f);
                    RandTex.SetPixel(x, y, new Color(r, g, b, a) );
                }
            }

            RandTex.Apply();
        }
        RandTex.filterMode=FilterMode.Trilinear;
        return RandTex;
    }

    List<Mesh> createMeshOld(int trinum = 0x10000)
    {
        List<Mesh> meshes = new List<Mesh>();
          int maxMeshSize = 0x10000/3*3;
          int mnum = (trinum*3+maxMeshSize-1)/maxMeshSize;
          for(int j=0;j<mnum;j++)
          {
            Mesh mesh = new Mesh();
            meshes.Add(mesh);
            mesh.Clear();
            int vnum = maxMeshSize;
            Vector3[] verts = new Vector3 [vnum];
            int[] tris  = new int [vnum];
            for(int i=0;i<vnum;i++)
            {
                verts[i].x=i+j*maxMeshSize;
                verts[i].y=1;
                verts[i].z=2;
                tris[i]=i;
            }
            mesh.vertices = verts;
            mesh.triangles = tris;
          }
          return meshes;
    }

    List<Mesh> createMesh(int trinum = 0x10000)
    {
        int maxMeshSize=0x10000/3*3;
        List<Mesh> meshes = new List<Mesh>();
          int num=trinum*3;
          for(int j=0;num>0;j++)
          {
            Mesh mesh = new Mesh();
            mesh.Clear();
            int vnum = Math.Min(num,maxMeshSize);
            Vector3[] verts = new Vector3 [vnum];
            int[] tris  = new int [vnum];
            for(int i=0;i<vnum;i++)
            {
                verts[i].x=i+j*maxMeshSize;
                verts[i].y=1;
                verts[i].z=2;
                tris[i]=i;
            }
            mesh.vertices = verts;
            mesh.triangles = tris;
            num-=vnum;
            meshes.Add(mesh);
          }
          return meshes;
    }

    int getMeshNumTriangles(List <Mesh> list)
    {
        int tcnt=0;
        for (int i = 0; i < list.Count; i++)
        {
            tcnt+=list[i].triangles.Length/3;
        }
        return tcnt;
    }

    void initMainMipmapRenderTexture(RenderTexture src)
    {
        if(mainMip == null)
        {
            mainMip = new RenderTexture(src.width, src.height,0,RenderTextureFormat.ARGB32);
            mainMip.antiAliasing=1; // must be for mipmapping to work!!
            mainMip.useMipMap=true;
            mainMip.filterMode=FilterMode.Trilinear;
#if UNITY_5_5_OR_NEWER
            //rtmip.autoGenerateMips=false;
#endif
        }

    }

    void initAll(int width, int height)
    {
        if(s.renderToTexture)
        {
            RenderTexture rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat);
            if(s.outputMipmap)
            {
                rt.antiAliasing=1; // must be for mipmapping to work!!
                rt.useMipMap=true;
                rt.filterMode=FilterMode.Trilinear;
            }
            s.outputTexture=rt;
        }
        else
            s.outputTexture=null;

        actWidth=width;
        actHeight=height;
        bufferOrder.Clear();
        textureCh.Clear();
        buffers.Clear();
        shaders.Clear();
        meshes.Clear();
        if(!textures.ContainsKey("rand256")) textures["rand256"] = createRandTex(256,256);
        if(!textures.ContainsKey("rand64"))  textures["rand64"]  = createRandTex(64,64);

        // check for define suffixes (appended to shader name - makes it possible to have different versions or "modes" of shader)
        List <string> defineModeSuffixes = new List <string>();
        foreach( var m in defineMode ){
            defineModeSuffixes.Add("__"+m.Key+"_"+m.Value);
        }

        bufferOrder.Add("Image");
        buffers["Image"] = createRenderTex();
        textureCh["Image"] = new Dictionary <int,string> ();
        textureDemandsMip["Image"] = new Dictionary <int,bool> ();
        shaders["Image"] = createShaderOptSuff("flockaroo_VanGogh/ImageHDRP",defineModeSuffixes);
        textureCh["Image"][0] = "Buff_A";
        textureCh["Image"][1] = "Ref:Buff_A:Tex0";
        bufferOrder.Add("Buff_A");
        buffers["Buff_A"] = createRenderTex();
        textureCh["Buff_A"] = new Dictionary <int,string> ();
        textureDemandsMip["Buff_A"] = new Dictionary <int,bool> ();
        shaders["Buff_A"] = createShaderOptSuff("flockaroo_VanGogh/Buff_AHDRP",defineModeSuffixes);
        textureCh["Buff_A"][0] = "https://www.shaderoo.org/textures/10xfx_trailer.webm";
        textureCh["Buff_A"][1] = "rand256";
        buffers["Buff_A"].useMipMap=true;
        buffers["Buff_A"].filterMode=FilterMode.Trilinear;
        textureDemandsMip["Buff_A"][0]=true;
        //###InitMarker
        // make sure image is rendered last
        int idxImage=bufferOrder.IndexOf("Image");
        if(idxImage>=0)
        {
            bufferOrder.RemoveAt(idxImage);
            bufferOrder.Add("Image");
        }
        screenQuadMesh=createMesh(2);
    }

    void myStart () {
        //initAll(Screen.width,Screen.height);
    }
    
    // Update is called once per frame
    void myUpdate () {
    	
    }

    Texture getTexture(string name)
    {
        if(name.StartsWith("Ref:")) {
            Match match = refRegex.Match(name);
            if (match.Success)
            {
                string buff = match.Groups[1].Value;
                int chan = int.Parse(match.Groups[2].Value);
                return getTexture(textureCh[buff][chan]);
            }
            return null;
        }
        if(buffers.ContainsKey(name))  return buffers[name];
        if(textures.ContainsKey(name)) return textures[name];
        if(name.EndsWith(".mp4"))      return mainTex;
        if(name.EndsWith(".webm"))     return mainTex;
        return mainTex;
        // FIXME: alloc textures if not present
        //return textures["rand256"];
    }

    //public bool IsActive() => MasterFade.value > 0f;

    public void Render(CommandBuffer cmd, RenderTexture src, RenderTexture dest) {
        src.filterMode=FilterMode.Trilinear;

        //mainTex=src;
        bool reinit=false;

        if(mySrc==null || mySrc.width!=src.width || mySrc.height!=src.height)
        {
            mySrc = new RenderTexture(src.width, src.height, 0, src.graphicsFormat);
            mySrc.filterMode=FilterMode.Bilinear;
            mySrc2 = new RenderTexture(src.width, src.height, 0, src.graphicsFormat);
            mySrc2.filterMode=FilterMode.Bilinear;
        }
        if(gammaShader==null) gammaShader = createShader("flockaroo_VanGogh/GammaCorrectShader");

        mainTex = mySrc;

        if(s.HDRPGamma==false || DoHDRPGamma==false)
        {
            cmd.CopyTexture(src, 0, mySrc, 0);
        }
        else
        {
            cmd.CopyTexture(src, 0, mySrc2, 0);
            gammaShader.SetFloat("gamma",1.0f/2.2f);
            cmd.Blit(mySrc2,mySrc,gammaShader);
        }

        if(s.inputTexture)
        {
            if(myInputTex==null || myInputTex.width!=s.inputTexture.width || myInputTex.height!=s.inputTexture.height)
            {
                myInputTex = new RenderTexture(s.inputTexture.width, s.inputTexture.height, 0, RenderTextureFormat.ARGBFloat);
                reinit=true;
            }
            //cmd.CopyTexture(inputTexture.value, 0, myInputTex, 0);
            if(s.HDRPGamma==false || DoHDRPGamma==false)
                cmd.Blit(s.inputTexture, myInputTex);
            else
            {
                gammaShader.SetFloat("gamma",1.0f/2.2f);
                cmd.Blit(s.inputTexture, myInputTex,gammaShader);
            }
            //cmd.Blit(myInputTex,mySrc,gammaShader);
            mainTex=myInputTex;
        }

        if (s.renderToTexture  && s.outputTexture==null) { reinit=true; }
        if (!s.renderToTexture && s.outputTexture!=null) { reinit=true; }

        // reinit if any defineMode changed

        if(mainTex.width!=actWidth || mainTex.height!=actHeight || reinit)
        {
            Debug.Log("Aquarelle 1st init (or Resolution changed)");
            initAll(mainTex.width,mainTex.height);
        }

        //mainTex=src;
        if(useMipOnMain)
        {
            initMainMipmapRenderTexture(mainTex);
            cmd.Blit(mainTex, mainMip);
            mainTex = mainMip;
        }

        foreach( string buffName in bufferOrder )
        {
            Material mat = null;
            if(shaders.ContainsKey(buffName)) mat = shaders[buffName];
            if(mat==null) { continue; }

            mat.SetFloat("geomFlipY", s.geomFlipY?1.0f:0.0f);
            mat.SetFloat("flipY", s.flipY?1.0f:0.0f);
            mat.SetInt("_FrameCount", Time.frameCount);
            mat.SetFloat("iResolutionWidth", actWidth);
            mat.SetFloat("iResolutionHeight", actHeight);

            mat.SetFloat("MasterFade",s.MasterFade);
            mat.SetFloat("diffuseStrength",s.diffuseStrength);
            mat.SetFloat("smearStrength",s.smearStrength);
            mat.SetFloat("specularStrength",s.specularStrength);
            mat.SetFloat("strokeAngle",s.strokeAngle);
            mat.SetFloat("NumSamples",s.NumSamples);
            mat.SetFloat("vignette",s.vignette);
            //###MatParams

            for(int i=0;i<8;i++)
            {
                if(textureCh.ContainsKey(buffName) &&
                   textureCh[buffName].ContainsKey(i))
                {
                    Texture tex = getTexture(textureCh[buffName][i]);
                    if(mat!=null) mat.SetTexture("iChannel"+i, tex);
                    if(textureDemandsMip.ContainsKey(buffName) &&
                       textureDemandsMip[buffName].ContainsKey(i) &&
                       textureDemandsMip[buffName][i])
                    {
                        if(tex==mainTex) useMipOnMain=true;
                        else if(tex is RenderTexture) ((RenderTexture)tex).useMipMap=true;
                    }
                }
           }

            if(meshes.ContainsKey(buffName))
            {
                cmd.SetRenderTarget(buffers[buffName]);
                //GL.Clear(true, true, Color.clear);
                cmd.ClearRenderTarget(true, true, Color.clear);
                if(mat!=null) mat.SetPass(0);
                if(meshNumTri.ContainsKey(buffName))
                {
                    int actTriNum=meshNumTri[buffName];
                    if(getMeshNumTriangles(meshes[buffName])!=actTriNum)
                    {
                        Debug.Log("resizeing mesh to "+actTriNum);
                        meshes[buffName]=createMesh(actTriNum);
                    }
                    mat.SetFloat("iNumTriangles",meshNumTri[buffName]);
                }
                foreach(Mesh mesh in meshes[buffName])
                {
                    //cmd.DrawMeshNow(mesh, Vector3.zero, Quaternion.identity);
                    cmd.DrawMesh(mesh, Matrix4x4.identity, mat, 0, -1, null);
                }
            }
            else
            {
                if(mat!=null)
                {
                    if(buffName=="Image")
                    {
                        mat.SetFloat("HDRPGamma",s.HDRPGamma?2.2f:1.0f);
                        if(s.outputTexture)
                        {
                            //cmd.Blit(mainTex, outputTexture, mat);
                            cmd.SetRenderTarget(s.outputTexture);
                            cmd.DrawMesh(screenQuadMesh[0], Matrix4x4.identity, mat, 0, -1, null);
                            // default blit of screen - no effect
                            cmd.Blit(mainTex, dest);
                        }
                        else
                        {
                            //cmd.Blit(mainTex, dest, mat);
                            //cmd.Blit(mainTex, mySrc2, mat);
                            //gammaShader.SetFloat("gamma",2.2f);
                            //cmd.Blit(mySrc2, dest, gammaShader);
                            cmd.SetRenderTarget(dest);
                            cmd.DrawMesh(screenQuadMesh[0], Matrix4x4.identity, mat, 0, -1, null);
                        }
                    }
                    else
                    {
                        //if(mat!=null) cmd.Blit(mainTex, buffers[buffName], mat);
                        cmd.SetRenderTarget(buffers[buffName]);
                        cmd.DrawMesh(screenQuadMesh[0], Matrix4x4.identity, mat, 0, -1, null);
                    }
                }
            }
        }

    }

    //------------------ RenderPass override funtions
    public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
    {
        // create a temporary render texture that matches the camera
        //cmd.GetTemporaryRT(tempTexture.id, cameraTextureDescriptor);
        //cmd.GetTemporaryRT(tempTexture2.id, cameraTextureDescriptor);
        if(tmpRT1==null) tmpRT1=RenderTexture.GetTemporary(cameraTextureDescriptor);
        if(tmpRT2==null) tmpRT2=RenderTexture.GetTemporary(cameraTextureDescriptor);
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData) {
        CommandBuffer cmd = CommandBufferPool.Get(profilerTag);
        //cmd.Blit(cameraColorTargetIdent,tempTexture2.Identifier());
        //Render(cmd,tempTexture2,tempTexture);
        //cmd.Blit(tempTexture.Identifier(), cameraColorTargetIdent);
        cmd.Blit(cameraColorTargetIdent,tmpRT1);
        Render(cmd,tmpRT1,tmpRT2);
        cmd.Blit(tmpRT2, cameraColorTargetIdent);
        context.ExecuteCommandBuffer(cmd);
        cmd.Clear();
        CommandBufferPool.Release(cmd);
    }

    public override void FrameCleanup(CommandBuffer cmd)
    {
        //RenderTexture.ReleaseTemporary(tmpRT1);
        //RenderTexture.ReleaseTemporary(tmpRT2);
    }
    } //class EffectRenderPass

    EffectRenderPass m_ScriptablePass;

    public override void Create()
    {
        m_ScriptablePass = new EffectRenderPass("FlockarooVanGogh",ref settings,settings.WhenToApply);
        //m_ScriptablePass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }

    // Here you can inject one or multiple render passes in the renderer.
    // This method is called when setting up the renderer once per-camera.
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        //if (!settings.IsEnabled)
        //{
        //    // we can do nothing this frame if we want
        //    return;
        //}
    
        // Gather up and pass any extra information our pass will need.
        // In this case we're getting the camera's color buffer target
        var cameraColorTargetIdent = renderer.cameraColorTarget;
        m_ScriptablePass.cameraColorTargetIdent=cameraColorTargetIdent;

        renderer.EnqueuePass(m_ScriptablePass);
    }
} // class

} // namespace
#endif
