# The hinge against the clip: where our rocked magazine sits (bone fixed, turned about its own origin) vs the clip's own pose
import sys,json,os
sys.path.insert(0,'.')
exec(open('gen_holds.py',encoding='utf-8').read().split("if __name__=='__main__':")[0])
held=json.load(open('out/ReloadHolds.json'))['rockIn']
for folder,prefix in (('ak74','ak74n'),('akm','akm'),('saiga12','saiga12k_10'),('svd','svd_s')):
    d=ROOT+folder+'/'
    clips={}
    for o in UnityPy.load(d+'client_assets.bundle').objects:
        if o.type.name=='AnimationClip':
            try:
                c=o.read(); clips[c.m_Name]=c
            except Exception: pass
    tree=None
    for c in sorted(os.listdir(d)):
        if c.endswith('_container.bundle') and c.startswith('weapon_'):
            t=loadTree(d+c)
            if bone(t[0],'/mod_magazine') and bone(t[0],'/Base HumanLPalm'):
                tree=t; break
    mag=bone(tree[0],'/mod_magazine'); name=f'{prefix}_mag_out_0' if f'{prefix}_mag_out_0' in clips else f'{prefix}_mag_out'
    rows=magazinePath(setup(tree,clips[name]),mag); seat=rows[0]; h=held[prefix]
    axis=np.array(h['axis'],float); half=math.radians(h['angle'])/2
    delta=np.array([axis[0]*math.sin(half),axis[1]*math.sin(half),axis[2]*math.sin(half),math.cos(half)])
    ours=qmul(seat['rot'],delta)
    g=setup(tree,clips[name])
    near=[r for r in rows if np.linalg.norm(r['pos']-seat['pos'])<=0.012]; tilted=max(near,key=lambda r:ang(r['rot'],seat['rot']))
    # 15 cm down the magazine, in the seated bone's frame found from the gun frame's down (-z)
    downInBone=g['rotate'](conj(seat['rot']),np.array([0,0,-0.15]))
    seatedPoint=g['rotate'](seat['rot'],downInBone)+seat['pos']
    oursPoint=g['rotate'](ours,downInBone)+seat['pos']
    clipPoint=g['rotate'](tilted['rot'],downInBone)+tilted['pos']
    moved=oursPoint-seatedPoint
    print(f"{prefix:12s} at full tilt {h['angle']} deg, a point 15 cm down the magazine moves cm: side {moved[0]*100:5.1f} forward {-moved[1]*100:5.1f} up {moved[2]*100:5.1f} | ours vs clip: {np.linalg.norm(oursPoint-clipPoint)*100:4.1f} cm, {ang(ours,tilted['rot']):4.1f} deg at t={tilted['t']:.2f}")
