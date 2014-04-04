//! Normalized pixel coordinates.
in vec2 vPixel;

//! Origin of viewing ray in left-top corner.
uniform vec3 uOriginLT;
//! Origin of viewing ray in left-bottom corner.
uniform vec3 uOriginLB;
//! Origin of viewing ray in right-top corner.
uniform vec3 uOriginRT;
//! Origin of viewing ray in right-bottom corner.
uniform vec3 uOriginRB;

//! Direction of viewing ray in left-top corner.
uniform vec3 uDirectLT;
//! Direction of viewing ray in left-bottom corner.
uniform vec3 uDirectLB;
//! Direction of viewing ray in right-top corner.
uniform vec3 uDirectRT;
//! Direction of viewing ray in right-bottom corner.
uniform vec3 uDirectRB;

//! Texture buffer of data records of high-level BVH nodes.
uniform isamplerBuffer uSceneNodeInfoTexture;
//! Texture buffer of minimum points of high-level BVH nodes.
uniform samplerBuffer uSceneMinPointTexture;
//! Texture buffer of maximum points of high-level BVH nodes.
uniform samplerBuffer uSceneMaxPointTexture;

//! Texture buffer of data records of bottom-level BVH nodes.
uniform isamplerBuffer uObjectNodeInfoTexture;
//! Texture buffer of minimum points of bottom-level BVH nodes.
uniform samplerBuffer uObjectMinPointTexture;
//! Texture buffer of maximum points of bottom-level BVH nodes.
uniform samplerBuffer uObjectMaxPointTexture;

//! Texture buffer of vertex coords.
uniform samplerBuffer uGeometryVertexTexture;
//! Texture buffer of vertex normals.
uniform samplerBuffer uGeometryNormalTexture;
//! Texture buffer of triangle indices.
uniform isamplerBuffer uGeometryTriangTexture;

//! Texture buffer of material properties.
uniform samplerBuffer uRaytraceMaterialTexture;
//! Texture buffer of light source properties.
uniform samplerBuffer uRaytraceLightSrcTexture;
//! Environment map texture.
uniform sampler2D uEnvironmentMapTexture;

//! Total number of light sources.
uniform int uLightCount;
//! Intensity of global ambient light.
uniform vec4 uGlobalAmbient;

//! Enables/disables environment map.
uniform int uEnvironmentEnable;
//! Enables/disables computation of shadows.
uniform int uShadowsEnable;
//! Enables/disables computation of reflections.
uniform int uReflectionsEnable;

//! Radius of bounding sphere of the scene.
uniform float uSceneRadius;
//! Scene epsilon to prevent self-intersections.
uniform float uSceneEpsilon;

/////////////////////////////////////////////////////////////////////////////////////////
// Specific data types
  
//! Stores ray parameters.
struct SRay
{
  vec3 Origin;
  
  vec3 Direct;
};

//! Stores intersection parameters.
struct SIntersect
{
  float Time;
  
  vec2 UV;
  
  vec3 Normal;
};

/////////////////////////////////////////////////////////////////////////////////////////
// Some useful constants

#define MAXFLOAT 1e15f

#define SMALL vec3 (exp2 (-80.f))

#define ZERO vec3 (0.f, 0.f, 0.f)
#define UNIT vec3 (1.f, 1.f, 1.f)

#define AXIS_X vec3 (1.f, 0.f, 0.f)
#define AXIS_Y vec3 (0.f, 1.f, 0.f)
#define AXIS_Z vec3 (0.f, 0.f, 1.f)

/////////////////////////////////////////////////////////////////////////////////////////
// Functions for compute ray-object intersection

// =======================================================================
// function : GenerateRay
// purpose  :
// =======================================================================
SRay GenerateRay (in vec2 thePixel)
{
  vec3 aP0 = mix (uOriginLB, uOriginRB, thePixel.x);
  vec3 aP1 = mix (uOriginLT, uOriginRT, thePixel.x);

  vec3 aD0 = mix (uDirectLB, uDirectRB, thePixel.x);
  vec3 aD1 = mix (uDirectLT, uDirectRT, thePixel.x);
  
  return SRay (mix (aP0, aP1, thePixel.y),
               mix (aD0, aD1, thePixel.y));
}

// =======================================================================
// function : IntersectSphere
// purpose  : Computes ray-sphere intersection
// =======================================================================
float IntersectSphere (in SRay theRay, in float theRadius)
{
  float aDdotD = dot (theRay.Direct, theRay.Direct);
  float aDdotO = dot (theRay.Direct, theRay.Origin);
  float aOdotO = dot (theRay.Origin, theRay.Origin);
  
  float aD = aDdotO * aDdotO - aDdotD * (aOdotO - theRadius * theRadius);
  
  if (aD > 0.f)
  {
    float aTime = (sqrt (aD) - aDdotO) * (1.f / aDdotD);
    
    return aTime > 0.f ? aTime : MAXFLOAT;
  }
  
  return MAXFLOAT;
}

// =======================================================================
// function : IntersectTriangle
// purpose  : Computes ray-triangle intersection (branchless version)
// =======================================================================
float IntersectTriangle (in SRay theRay,
                         in vec3 thePnt0,
                         in vec3 thePnt1,
                         in vec3 thePnt2,
                         out vec2 theUV,
                         out vec3 theNorm)
{
  vec3 aEdge0 = thePnt1 - thePnt0;
  vec3 aEdge1 = thePnt0 - thePnt2;
  
  theNorm = cross (aEdge1, aEdge0);

  vec3 aEdge2 = (1.f / dot (theNorm, theRay.Direct)) * (thePnt0 - theRay.Origin);
  
  float aTime = dot (theNorm, aEdge2);

  vec3 theVec = cross (theRay.Direct, aEdge2);
  
  theUV.x = dot (theVec, aEdge1);
  theUV.y = dot (theVec, aEdge0);
  
  return bool (int(aTime >= 0.f) &
               int(theUV.x >= 0.f) &
               int(theUV.y >= 0.f) &
               int(theUV.x + theUV.y <= 1.f)) ? aTime : MAXFLOAT;
}

//! Global stack shared between traversal functions.
int Stack[STACK_SIZE];

//! Identifies the absence of intersection.
#define INALID_HIT ivec4 (-1)

// =======================================================================
// function : ObjectNearestHit
// purpose  : Finds intersection with nearest object triangle
// =======================================================================
ivec4 ObjectNearestHit (in int theBVHOffset, in int theVrtOffset, in int theTrgOffset,
  in SRay theRay, in vec3 theInverse, inout SIntersect theHit, in int theSentinel)
{
  int aHead = theSentinel; // stack pointer
  int aNode = 0;           // node to visit

  ivec4 aTriIndex = INALID_HIT;

  float aTimeOut;
  float aTimeLft;
  float aTimeRgh;

  while (true)
  {
    ivec3 aData = texelFetch (uObjectNodeInfoTexture, aNode + theBVHOffset).xyz;

    if (aData.x == 0) // if inner node
    {
      vec3 aNodeMinLft = texelFetch (uObjectMinPointTexture, aData.y + theBVHOffset).xyz;
      vec3 aNodeMaxLft = texelFetch (uObjectMaxPointTexture, aData.y + theBVHOffset).xyz;
      vec3 aNodeMinRgh = texelFetch (uObjectMinPointTexture, aData.z + theBVHOffset).xyz;
      vec3 aNodeMaxRgh = texelFetch (uObjectMaxPointTexture, aData.z + theBVHOffset).xyz;

      vec3 aTime0 = (aNodeMinLft - theRay.Origin) * theInverse;
      vec3 aTime1 = (aNodeMaxLft - theRay.Origin) * theInverse;
      
      vec3 aTimeMax = max (aTime0, aTime1);
      vec3 aTimeMin = min (aTime0, aTime1);

      aTime0 = (aNodeMinRgh - theRay.Origin) * theInverse;
      aTime1 = (aNodeMaxRgh - theRay.Origin) * theInverse;
      
      aTimeOut = min (aTimeMax.x, min (aTimeMax.y, aTimeMax.z));
      aTimeLft = max (aTimeMin.x, max (aTimeMin.y, aTimeMin.z));

      int aHitLft = int(aTimeLft <= aTimeOut) & int(aTimeOut >= 0.f) & int(aTimeLft <= theHit.Time);

      aTimeMax = max (aTime0, aTime1);
      aTimeMin = min (aTime0, aTime1);

      aTimeOut = min (aTimeMax.x, min (aTimeMax.y, aTimeMax.z));
      aTimeRgh = max (aTimeMin.x, max (aTimeMin.y, aTimeMin.z));

      int aHitRgh = int(aTimeRgh <= aTimeOut) & int(aTimeOut >= 0.f) & int(aTimeRgh <= theHit.Time);

      if (bool(aHitLft & aHitRgh))
      {
        aNode = (aTimeLft < aTimeRgh) ? aData.y : aData.z;
        
        Stack[++aHead] = (aTimeLft < aTimeRgh) ? aData.z : aData.y;
      }
      else
      {
        if (bool(aHitLft | aHitRgh))
        {
          aNode = bool(aHitLft) ? aData.y : aData.z;
        }
        else
        {
          if (aHead == theSentinel)
            return aTriIndex;
            
          aNode = Stack[aHead--];
        }
      }
    }
    else // if leaf node
    {
      vec3 aNormal;
      vec2 aParams;
            
      for (int anIdx = aData.y; anIdx <= aData.z; ++anIdx)
      {
        ivec4 aTriangle = texelFetch (uGeometryTriangTexture, anIdx + theTrgOffset);

        vec3 aPoint0 = texelFetch (uGeometryVertexTexture, aTriangle.x + theVrtOffset).xyz;
        vec3 aPoint1 = texelFetch (uGeometryVertexTexture, aTriangle.y + theVrtOffset).xyz;
        vec3 aPoint2 = texelFetch (uGeometryVertexTexture, aTriangle.z + theVrtOffset).xyz;

        float aTime = IntersectTriangle (theRay,
                                         aPoint0,
                                         aPoint1,
                                         aPoint2,
                                         aParams,
                                         aNormal);
                                         
        if (aTime < theHit.Time)
        {
          aTriIndex = aTriangle;
          
          theHit = SIntersect (aTime, aParams, aNormal);
        }
      }
      
      if (aHead == theSentinel)
        return aTriIndex;

      aNode = Stack[aHead--];
    }
  }

  return aTriIndex;
}

// =======================================================================
// function : ObjectAnyHit
// purpose  : Finds intersection with any object triangle
// =======================================================================
float ObjectAnyHit (in int theBVHOffset, in int theVrtOffset, in int theTrgOffset,
  in SRay theRay, in vec3 theInverse, in float theDistance, in int theSentinel)
{
  int aHead = theSentinel; // stack pointer
  int aNode = 0;           // node to visit

  float aTimeOut;
  float aTimeLft;
  float aTimeRgh;

  while (true)
  {
    ivec4 aData = texelFetch (uObjectNodeInfoTexture, aNode + theBVHOffset);

    if (aData.x == 0) // if inner node
    {
      vec3 aNodeMinLft = texelFetch (uObjectMinPointTexture, aData.y + theBVHOffset).xyz;
      vec3 aNodeMaxLft = texelFetch (uObjectMaxPointTexture, aData.y + theBVHOffset).xyz;
      vec3 aNodeMinRgh = texelFetch (uObjectMinPointTexture, aData.z + theBVHOffset).xyz;
      vec3 aNodeMaxRgh = texelFetch (uObjectMaxPointTexture, aData.z + theBVHOffset).xyz;

      vec3 aTime0 = (aNodeMinLft - theRay.Origin) * theInverse;
      vec3 aTime1 = (aNodeMaxLft - theRay.Origin) * theInverse;

      vec3 aTimeMax = max (aTime0, aTime1);
      vec3 aTimeMin = min (aTime0, aTime1);

      aTime0 = (aNodeMinRgh - theRay.Origin) * theInverse;
      aTime1 = (aNodeMaxRgh - theRay.Origin) * theInverse;
      
      aTimeOut = min (aTimeMax.x, min (aTimeMax.y, aTimeMax.z));
      aTimeLft = max (aTimeMin.x, max (aTimeMin.y, aTimeMin.z));

      int aHitLft = int(aTimeLft <= aTimeOut) & int(aTimeOut >= 0.f) & int(aTimeLft <= theDistance);

      aTimeMax = max (aTime0, aTime1);
      aTimeMin = min (aTime0, aTime1);

      aTimeOut = min (aTimeMax.x, min (aTimeMax.y, aTimeMax.z));
      aTimeRgh = max (aTimeMin.x, max (aTimeMin.y, aTimeMin.z));

      int aHitRgh = int(aTimeRgh <= aTimeOut) & int(aTimeOut >= 0.f) & int(aTimeRgh <= theDistance);

      if (bool(aHitLft & aHitRgh))
      {
        aNode = (aTimeLft < aTimeRgh) ? aData.y : aData.z;

        Stack[++aHead] = (aTimeLft < aTimeRgh) ? aData.z : aData.y;
      }
      else
      {
        if (bool(aHitLft | aHitRgh))
        {
          aNode = bool(aHitLft) ? aData.y : aData.z;
        }
        else
        {
          if (aHead == theSentinel)
            return 1.f;

          aNode = Stack[aHead--];
        }
      }
    }
    else // if leaf node
    {
      vec3 aNormal;
      vec2 aParams;
      
      for (int anIdx = aData.y; anIdx <= aData.z; ++anIdx)
      {
        ivec4 aTriangle = texelFetch (uGeometryTriangTexture, anIdx + theTrgOffset);

        vec3 aPoint0 = texelFetch (uGeometryVertexTexture, aTriangle.x + theVrtOffset).xyz;
        vec3 aPoint1 = texelFetch (uGeometryVertexTexture, aTriangle.y + theVrtOffset).xyz;
        vec3 aPoint2 = texelFetch (uGeometryVertexTexture, aTriangle.z + theVrtOffset).xyz;

        float aTime = IntersectTriangle (theRay,
                                         aPoint0,
                                         aPoint1,
                                         aPoint2,
                                         aParams,
                                         aNormal);
                                         
        if (aTime < theDistance)
          return 0.f;
      }
      
      if (aHead == theSentinel)
        return 1.f;

      aNode = Stack[aHead--];
    }
  }

  return 1.f;
}

// =======================================================================
// function : SceneNearestHit
// purpose  : Finds intersection with nearest scene triangle
// =======================================================================
ivec4 SceneNearestHit (in SRay theRay, in vec3 theInverse, inout SIntersect theHit)
{
  int aHead = -1; // stack pointer
  int aNode =  0; // node to visit

  ivec4 aHitObject = INALID_HIT;
  
  float aTimeOut;
  float aTimeLft;
  float aTimeRgh;

  while (true)
  {
    ivec4 aData = texelFetch (uSceneNodeInfoTexture, aNode);

    if (aData.x != 0) // if leaf node
    {
      vec3 aNodeMin = texelFetch (uSceneMinPointTexture, aNode).xyz;
      vec3 aNodeMax = texelFetch (uSceneMaxPointTexture, aNode).xyz;
      
      vec3 aTime0 = (aNodeMin - theRay.Origin) * theInverse;
      vec3 aTime1 = (aNodeMax - theRay.Origin) * theInverse;
      
      vec3 aTimes = min (aTime0, aTime1);
      
      if (max (aTimes.x, max (aTimes.y, aTimes.z)) < theHit.Time)
      {
        ivec4 aTriIndex = ObjectNearestHit (
          aData.y, aData.z, aData.w, theRay, theInverse, theHit, aHead);

        if (aTriIndex.x != -1)
        {
          aHitObject = ivec4 (aTriIndex.x + aData.z,  // vertex 0
                              aTriIndex.y + aData.z,  // vertex 1
                              aTriIndex.z + aData.z,  // vertex 2
                              aTriIndex.w);           // material
        }
      }
      
      if (aHead < 0)
        return aHitObject;
            
      aNode = Stack[aHead--];
    }
    else // if inner node
    {
      vec3 aNodeMinLft = texelFetch (uSceneMinPointTexture, aData.y).xyz;
      vec3 aNodeMaxLft = texelFetch (uSceneMaxPointTexture, aData.y).xyz;
      vec3 aNodeMinRgh = texelFetch (uSceneMinPointTexture, aData.z).xyz;
      vec3 aNodeMaxRgh = texelFetch (uSceneMaxPointTexture, aData.z).xyz;

      vec3 aTime0 = (aNodeMinLft - theRay.Origin) * theInverse;
      vec3 aTime1 = (aNodeMaxLft - theRay.Origin) * theInverse;

      vec3 aTimeMax = max (aTime0, aTime1);
      vec3 aTimeMin = min (aTime0, aTime1);

      aTimeOut = min (aTimeMax.x, min (aTimeMax.y, aTimeMax.z));
      aTimeLft = max (aTimeMin.x, max (aTimeMin.y, aTimeMin.z));

      int aHitLft = int(aTimeLft <= aTimeOut) & int(aTimeOut >= 0.f) & int(aTimeLft <= theHit.Time);
      
      aTime0 = (aNodeMinRgh - theRay.Origin) * theInverse;
      aTime1 = (aNodeMaxRgh - theRay.Origin) * theInverse;

      aTimeMax = max (aTime0, aTime1);
      aTimeMin = min (aTime0, aTime1);

      aTimeOut = min (aTimeMax.x, min (aTimeMax.y, aTimeMax.z));
      aTimeRgh = max (aTimeMin.x, max (aTimeMin.y, aTimeMin.z));
      
      int aHitRgh = int(aTimeRgh <= aTimeOut) & int(aTimeOut >= 0.f) & int(aTimeRgh <= theHit.Time);

      if (bool(aHitLft & aHitRgh))
      {
        aNode = (aTimeLft < aTimeRgh) ? aData.y : aData.z;

        Stack[++aHead] = (aTimeLft < aTimeRgh) ? aData.z : aData.y;
      }
      else
      {
        if (bool(aHitLft | aHitRgh))
        {
          aNode = bool(aHitLft) ? aData.y : aData.z;
        }
        else
        {
          if (aHead < 0)
            return aHitObject;

          aNode = Stack[aHead--];
        }
      }
    }
  }
  
  return aHitObject;
}

// =======================================================================
// function : SceneAnyHit
// purpose  : Finds intersection with any scene triangle
// =======================================================================
float SceneAnyHit (in SRay theRay, in vec3 theInverse, in float theDistance)
{
  int aHead = -1; // stack pointer
  int aNode =  0; // node to visit
  
  float aTimeOut;
  float aTimeLft;
  float aTimeRgh;

  while (true)
  {
    ivec4 aData = texelFetch (uSceneNodeInfoTexture, aNode);

    if (aData.x != 0) // if leaf node
    {
      bool isShadow = 0.f == ObjectAnyHit (
        aData.y, aData.z, aData.w, theRay, theInverse, theDistance, aHead);
        
      if (aHead < 0 || isShadow)
        return isShadow ? 0.f : 1.f;
            
      aNode = Stack[aHead--];
    }
    else // if inner node
    {
      vec3 aNodeMinLft = texelFetch (uSceneMinPointTexture, aData.y).xyz;
      vec3 aNodeMaxLft = texelFetch (uSceneMaxPointTexture, aData.y).xyz;
      vec3 aNodeMinRgh = texelFetch (uSceneMinPointTexture, aData.z).xyz;
      vec3 aNodeMaxRgh = texelFetch (uSceneMaxPointTexture, aData.z).xyz;
      
      vec3 aTime0 = (aNodeMinLft - theRay.Origin) * theInverse;
      vec3 aTime1 = (aNodeMaxLft - theRay.Origin) * theInverse;

      vec3 aTimeMax = max (aTime0, aTime1);
      vec3 aTimeMin = min (aTime0, aTime1);

      aTimeOut = min (aTimeMax.x, min (aTimeMax.y, aTimeMax.z));
      aTimeLft = max (aTimeMin.x, max (aTimeMin.y, aTimeMin.z));

      int aHitLft = int(aTimeLft <= aTimeOut) & int(aTimeOut >= 0.f) & int(aTimeLft <= theDistance);
      
      aTime0 = (aNodeMinRgh - theRay.Origin) * theInverse;
      aTime1 = (aNodeMaxRgh - theRay.Origin) * theInverse;

      aTimeMax = max (aTime0, aTime1);
      aTimeMin = min (aTime0, aTime1);

      aTimeOut = min (aTimeMax.x, min (aTimeMax.y, aTimeMax.z));
      aTimeRgh = max (aTimeMin.x, max (aTimeMin.y, aTimeMin.z));
      
      int aHitRgh = int(aTimeRgh <= aTimeOut) & int(aTimeOut >= 0.f) & int(aTimeRgh <= theDistance);

      if (bool(aHitLft & aHitRgh))
      {
        aNode = (aTimeLft < aTimeRgh) ? aData.y : aData.z;

        Stack[++aHead] = (aTimeLft < aTimeRgh) ? aData.z : aData.y;
      }
      else
      {
        if (bool(aHitLft | aHitRgh))
        {
          aNode = bool(aHitLft) ? aData.y : aData.z;
        }
        else
        {
          if (aHead < 0)
            return 1.f;

          aNode = Stack[aHead--];
        }
      }
    }
  }
  
  return 1.f;
}

#define PI 3.1415926f

// =======================================================================
// function : Latlong
// purpose  : Converts world direction to environment texture coordinates
// =======================================================================
vec2 Latlong (in vec3 thePoint, in float theRadius)
{
  float aPsi = acos (-thePoint.z / theRadius);
  
  float aPhi = atan (thePoint.y, thePoint.x) + PI;
  
  return vec2 (aPhi * 0.1591549f,
               aPsi * 0.3183098f);
}

// =======================================================================
// function : SmoothNormal
// purpose  : Interpolates normal across the triangle
// =======================================================================
vec3 SmoothNormal (in vec2 theUV, in ivec4 theTriangle)
{
  vec3 aNormal0 = texelFetch (uGeometryNormalTexture, theTriangle.x).xyz;
  vec3 aNormal1 = texelFetch (uGeometryNormalTexture, theTriangle.y).xyz;
  vec3 aNormal2 = texelFetch (uGeometryNormalTexture, theTriangle.z).xyz;
  
  return normalize (aNormal1 * theUV.x +
                    aNormal2 * theUV.y +
                    aNormal0 * (1.f - theUV.x - theUV.y));
}

#define THRESHOLD vec3 (0.1f, 0.1f, 0.1f)

#define MATERIAL_AMBN(index) (7 * index + 0)
#define MATERIAL_DIFF(index) (7 * index + 1)
#define MATERIAL_SPEC(index) (7 * index + 2)
#define MATERIAL_EMIS(index) (7 * index + 3)
#define MATERIAL_REFL(index) (7 * index + 4)
#define MATERIAL_REFR(index) (7 * index + 5)
#define MATERIAL_TRAN(index) (7 * index + 6)

#define LIGHT_POS(index) (2 * index + 1)
#define LIGHT_PWR(index) (2 * index + 0)

// =======================================================================
// function : Radiance
// purpose  : Computes color of specified ray
// =======================================================================
vec4 Radiance (in SRay theRay, in vec3 theInverse)
{
  vec3 aResult = vec3 (0.f);
  vec4 aWeight = vec4 (1.f);
  
  for (int aDepth = 0; aDepth < 5; ++aDepth)
  {
    SIntersect aHit = SIntersect (MAXFLOAT, vec2 (ZERO), ZERO);
    
    ivec4 aTriIndex = SceneNearestHit (theRay, theInverse, aHit);

    if (aTriIndex.x == -1)
    {
      if (aWeight.w != 0.f)
      {
        return vec4 (aResult.x,
                     aResult.y,
                     aResult.z,
                     aWeight.w);
      }

      if (bool(uEnvironmentEnable))
      {
        float aTime = IntersectSphere (theRay, uSceneRadius);
        
        aResult.xyz += aWeight.xyz * textureLod (uEnvironmentMapTexture,
          Latlong (theRay.Direct * aTime + theRay.Origin, uSceneRadius), 0.f).xyz;
      }
      
      return vec4 (aResult.x,
                   aResult.y,
                   aResult.z,
                   aWeight.w);
    }
    
    vec3 aPoint = theRay.Direct * aHit.Time + theRay.Origin;
    
    vec3 aAmbient = vec3 (texelFetch (
      uRaytraceMaterialTexture, MATERIAL_AMBN (aTriIndex.w)));
    vec3 aDiffuse = vec3 (texelFetch (
      uRaytraceMaterialTexture, MATERIAL_DIFF (aTriIndex.w)));
    vec4 aSpecular = vec4 (texelFetch (
      uRaytraceMaterialTexture, MATERIAL_SPEC (aTriIndex.w)));
    vec2 aOpacity = vec2 (texelFetch (
      uRaytraceMaterialTexture, MATERIAL_TRAN (aTriIndex.w)));
      
    vec3 aNormal = SmoothNormal (aHit.UV, aTriIndex);
    
    aHit.Normal = normalize (aHit.Normal);
    
    for (int aLightIdx = 0; aLightIdx < uLightCount; ++aLightIdx)
    {
      vec4 aLight = texelFetch (
        uRaytraceLightSrcTexture, LIGHT_POS (aLightIdx));
      
      float aDistance = MAXFLOAT;
      
      if (aLight.w != 0.f) // point light source
      {
        aDistance = length (aLight.xyz -= aPoint);
        
        aLight.xyz *= 1.f / aDistance;
      }

      SRay aShadow = SRay (aPoint + aLight.xyz * uSceneEpsilon, aLight.xyz);
      
      aShadow.Origin += aHit.Normal * uSceneEpsilon *
        (dot (aHit.Normal, aLight.xyz) >= 0.f ? 1.f : -1.f);
      
      float aVisibility = 1.f;
     
      if (bool(uShadowsEnable))
      {
        vec3 aInverse = 1.f / max (abs (aLight.xyz), SMALL);
        
        aInverse.x = aLight.x < 0.f ? -aInverse.x : aInverse.x;
        aInverse.y = aLight.y < 0.f ? -aInverse.y : aInverse.y;
        aInverse.z = aLight.z < 0.f ? -aInverse.z : aInverse.z;
        
        aVisibility = SceneAnyHit (aShadow, aInverse, aDistance);
      }
      
      if (aVisibility > 0.f)
      {
        vec3 aIntensity = vec3 (texelFetch (
          uRaytraceLightSrcTexture, LIGHT_PWR (aLightIdx)));
 
        float aLdotN = dot (aShadow.Direct, aNormal);
        
        if (aOpacity.y > 0.f)    // force two-sided lighting
          aLdotN = abs (aLdotN); // for transparent surfaces
          
        if (aLdotN > 0.f)
        {
          float aRdotV = dot (reflect (aShadow.Direct, aNormal), theRay.Direct);
          
          aResult.xyz += aWeight.xyz * aOpacity.x * aIntensity *
            (aDiffuse * aLdotN + aSpecular.xyz * pow (max (0.f, aRdotV), aSpecular.w));
        }
      }
    }
    
    aResult.xyz += aWeight.xyz * uGlobalAmbient.xyz *
      aAmbient * aOpacity.x * max (abs (dot (aNormal, theRay.Direct)), 0.5f);
    
    if (aOpacity.x != 1.f)
    {
      aWeight *= aOpacity.y;
    }
    else
    {
      aWeight *= bool(uReflectionsEnable) ?
        texelFetch (uRaytraceMaterialTexture, MATERIAL_REFL (aTriIndex.w)) : vec4 (0.f);
      
      theRay.Direct = reflect (theRay.Direct, aNormal);
      
      if (dot (theRay.Direct, aHit.Normal) < 0.f)
      {
        theRay.Direct = reflect (theRay.Direct, aHit.Normal);      
      }

      theInverse = 1.0 / max (abs (theRay.Direct), SMALL);
      
      theInverse.x = theRay.Direct.x < 0.0 ? -theInverse.x : theInverse.x;
      theInverse.y = theRay.Direct.y < 0.0 ? -theInverse.y : theInverse.y;
      theInverse.z = theRay.Direct.z < 0.0 ? -theInverse.z : theInverse.z;
      
      aPoint += aHit.Normal * (dot (aHit.Normal, theRay.Direct) >= 0.f ? uSceneEpsilon : -uSceneEpsilon);
    }
    
    if (all (lessThanEqual (aWeight.xyz, THRESHOLD)))
    {
      return vec4 (aResult.x,
                   aResult.y,
                   aResult.z,
                   aWeight.w);
    }
    
    theRay.Origin = theRay.Direct * uSceneEpsilon + aPoint;
  }

  return vec4 (aResult.x,
               aResult.y,
               aResult.z,
               aWeight.w);
}
