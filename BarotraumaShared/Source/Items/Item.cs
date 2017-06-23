﻿using Barotrauma.Items.Components;
using Barotrauma.Networking;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Contacts;
using Lidgren.Network;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma
{
    public enum ActionType
    {
        Always, OnPicked, OnUse, OnSecondaryUse,
        OnWearing, OnContaining, OnContained, 
        OnActive, OnFailure, OnBroken, 
        OnFire, InWater,
        OnImpact
    }

    partial class Item : MapEntity, IDamageable, IPropertyObject, IServerSerializable, IClientSerializable
    {
        const float MaxVel = 64.0f;

        public static List<Item> ItemList = new List<Item>();
        private ItemPrefab prefab;

        public static bool ShowLinks = true;
        
        private HashSet<string> tags;
        
        public Hull CurrentHull;
        
        public bool Visible = true;

        public SpriteEffects SpriteEffects = SpriteEffects.None;
        
        //components that determine the functionality of the item
        public List<ItemComponent> components;
        public List<IDrawableComponent> drawableComponents;

        public PhysicsBody body;
        
        private Vector2 lastSentPos;
        private bool prevBodyAwake;

        private bool needsPositionUpdate;
        private float lastSentCondition;

        private float condition;

        private bool inWater;
                
        private Inventory parentInventory;
        private Inventory ownInventory;

        private Dictionary<string, Connection> connections;

        //a dictionary containing lists of the status effects in all the components of the item
        private Dictionary<ActionType, List<StatusEffect>> statusEffectLists;
        
        public readonly Dictionary<string, ObjectProperty> properties;
        public Dictionary<string, ObjectProperty> ObjectProperties
        {
            get { return properties; }
        }

        private bool? hasInGameEditableProperties;
        bool HasInGameEditableProperties
        {
            get
            {
                if (hasInGameEditableProperties==null)
                {
                    hasInGameEditableProperties = GetProperties<InGameEditable>().Any();
                }
                return (bool)hasInGameEditableProperties;
            }
        }

        //the inventory in which the item is contained in
        public Inventory ParentInventory
        {
            get
            {
                return parentInventory;
            }
            set
            {
                parentInventory = value;

                if (parentInventory != null) Container = parentInventory.Owner as Item;                
            }
        }

        public Item Container
        {
            get;
            private set;
        }

        public override bool SelectableInEditor
        {
            get
            {
                return parentInventory == null && (body == null || body.Enabled);
            }
        }

        public List<FixRequirement> FixRequirements;

        public override string Name
        {
            get { return prefab.Name; }
        }

        public string Description
        {
            get { return prefab.Description; }
        }

        public float ImpactTolerance
        {
            get { return prefab.ImpactTolerance; }
        }

        public float PickDistance
        {
            get { return prefab.PickDistance; }
        }

        public override Vector2 SimPosition
        {
            get
            {
                return (body==null) ? base.SimPosition : body.SimPosition;
            }
        }
        public bool NeedsPositionUpdate
        {
            get
            {
                if (body == null || !body.Enabled) return false;
                return needsPositionUpdate;
            }
            set
            {
                needsPositionUpdate = value;
            }
        }

        protected Color spriteColor;
        [Editable, HasDefaultValue("1.0,1.0,1.0,1.0", true)]
        public string SpriteColor
        {
            get { return ToolBox.Vector4ToString(spriteColor.ToVector4()); }
            set
            {
                spriteColor = new Color(ToolBox.ParseToVector4(value));
            }
        }

        public Color Color
        {
            get { return spriteColor; }
        }

        public float Condition
        {
            get { return condition; }
            set 
            {
                if (GameMain.Client != null) return;
                if (!MathUtils.IsValid(value)) return;

                float prev = condition;
                condition = MathHelper.Clamp(value, 0.0f, 100.0f); 
                if (condition == 0.0f && prev>0.0f)
                {
                    ApplyStatusEffects(ActionType.OnBroken, 1.0f, null);
                    foreach (FixRequirement req in FixRequirements)
                    {
                        req.Fixed = false;
                    }
                }

                if (GameMain.Server != null && lastSentCondition != condition)
                {
                    if (Math.Abs(lastSentCondition - condition) > 1.0f || condition == 0.0f || condition == 100.0f)
                    {
                        GameMain.Server.CreateEntityEvent(this, new object[] { NetEntityEvent.Type.Status });
                        lastSentCondition = condition;
                    }
                }
            }
        }

        public float Health
        {
            get { return condition; }
        }

        [Editable, HasDefaultValue("", true)]
        public string Tags
        {
            get { return string.Join(",",tags); }
            set
            {
                tags.Clear();
                if (value == null) return;

                string[] newTags = value.Split(',');
                foreach (string tag in newTags)
                {
                    string newTag = tag.Trim();
                    if (!tags.Contains(newTag)) tags.Add(newTag);
                }   

            }
        }

        public bool FireProof
        {
            get { return prefab.FireProof; }
        }

        public bool CanUseOnSelf
        {
            get { return prefab.CanUseOnSelf; }
        }

        public bool InWater
        {
            get 
            { 
                //if the item has an active physics body, inWater is updated in the Update method
                if (body != null && body.Enabled) return inWater;

                //if not, we'll just have to check
                return IsInWater();
            }
        }
        
        public ItemPrefab Prefab
        {
            get { return prefab; }
        }

        public string ConfigFile
        {
            get { return prefab.ConfigFile; }
        }
        
        //which type of inventory slots (head, torso, any, etc) the item can be placed in
        public List<InvSlotType> AllowedSlots
        {
            get
            {
                Pickable p = GetComponent<Pickable>();
                return (p==null) ? new List<InvSlotType>() { InvSlotType.Any } : p.AllowedSlots;
            }
        }
        
        public List<Connection> Connections
        {
            get 
            {
                ConnectionPanel panel = GetComponent<ConnectionPanel>();
                if (panel == null) return null;
                return panel.Connections;
            }
        }

        public Item[] ContainedItems
        {
            get
            {
                return (ownInventory == null) ? null : Array.FindAll(ownInventory.Items, i => i != null);
            }
        }

        public override bool IsLinkable
        {
            get { return prefab.IsLinkable; }
        }

        public override string ToString()
        {
#if CLIENT
            return (GameMain.DebugDraw) ? Name + "(ID: " + ID + ")" : Name;
#elif SERVER
            return Name + "(ID: " + ID + ")";
#endif
        }

        public List<IPropertyObject> AllPropertyObjects
        {
            get
            {
                List<IPropertyObject> pobjects = new List<IPropertyObject>();
                pobjects.Add(this);
                foreach (ItemComponent ic in components)
                {
                    pobjects.Add(ic);
                }
                return pobjects;
            }
        }

        public Item(ItemPrefab itemPrefab, Vector2 position, Submarine submarine)
            : this(new Rectangle(
                (int)(position.X - itemPrefab.sprite.size.X / 2), 
                (int)(position.Y + itemPrefab.sprite.size.Y / 2), 
                (int)itemPrefab.sprite.size.X, 
                (int)itemPrefab.sprite.size.Y), 
            itemPrefab, submarine)
        {

        }

        public Item(Rectangle newRect, ItemPrefab itemPrefab, Submarine submarine)
            : base(itemPrefab, submarine)
        {
            prefab = itemPrefab;

            spriteColor = prefab.SpriteColor;

            linkedTo            = new ObservableCollection<MapEntity>();
            components          = new List<ItemComponent>();
            drawableComponents  = new List<IDrawableComponent>();
            FixRequirements     = new List<FixRequirement>();
            tags                = new HashSet<string>();
                       
            rect = newRect;
            
            if (submarine==null || !submarine.Loading) FindHull();

            condition = 100.0f;
            lastSentCondition = 100.0f;

            XElement element = prefab.ConfigElement;
            if (element == null) return;
            
            properties = ObjectProperty.InitProperties(this, element);

            foreach (XElement subElement in element.Elements())
            {
                switch (subElement.Name.ToString().ToLowerInvariant())
                {
                    case "body":
                        body = new PhysicsBody(subElement, ConvertUnits.ToSimUnits(Position));
                        body.FarseerBody.AngularDamping = 0.2f;
                        body.FarseerBody.LinearDamping  = 0.1f;
                        break;
                    case "trigger":
                    case "sprite":
                    case "deconstruct":
                        break;
                    case "aitarget":
                        aiTarget = new AITarget(this);
                        aiTarget.SightRange = ToolBox.GetAttributeFloat(subElement, "sightrange", 1000.0f);
                        aiTarget.SoundRange = ToolBox.GetAttributeFloat(subElement, "soundrange", 0.0f);
                        break;
                    case "fixrequirement":
                        FixRequirements.Add(new FixRequirement(subElement));
                        break;
                    default:
                        ItemComponent ic = ItemComponent.Load(subElement, this, prefab.ConfigFile);
                        if (ic == null) break;

                        components.Add(ic);

                        if (ic is IDrawableComponent && ic.Drawable) drawableComponents.Add(ic as IDrawableComponent);

                        if (ic.statusEffectLists == null) continue;

                        if (statusEffectLists == null) 
                            statusEffectLists = new Dictionary<ActionType, List<StatusEffect>>();

                        //go through all the status effects of the component 
                        //and add them to the corresponding statuseffect list
                        foreach (List<StatusEffect> componentEffectList in ic.statusEffectLists.Values)
                        {

                            ActionType actionType = componentEffectList.First().type;

                            List<StatusEffect> statusEffectList;
                            if (!statusEffectLists.TryGetValue(actionType, out statusEffectList))
                            {
                                statusEffectList = new List<StatusEffect>();
                                statusEffectLists.Add(actionType, statusEffectList);
                            }

                            foreach (StatusEffect effect in componentEffectList)
                            {
                                statusEffectList.Add(effect);
                            }
                        }

                        break;
                }
            }

            //cache connections into a dictionary for faster lookups
            var connectionPanel = GetComponent<ConnectionPanel>();
            if (connectionPanel != null)
            {
                connections = new Dictionary<string, Connection>();
                foreach (Connection c in connectionPanel.Connections)
                {
                    if (!connections.ContainsKey(c.Name))
                        connections.Add(c.Name, c);
                }
            }

            //containers need to handle collision events to notify items inside them about the impact
            var itemContainer = GetComponent<ItemContainer>();
            if (ImpactTolerance > 0.0f || itemContainer != null)
            {
                if (body != null) body.FarseerBody.OnCollision += OnCollision;
            }

            if (itemContainer != null)
            {
                ownInventory = itemContainer.Inventory;
            }

            InsertToList();
            ItemList.Add(this);
        }

        public override MapEntity Clone()
        {
            Item clone = new Item(rect, prefab, Submarine);
            foreach (KeyValuePair<string, ObjectProperty> property in properties)
            {
                if (!property.Value.Attributes.OfType<Editable>().Any()) continue;
                clone.properties[property.Key].TrySetValue(property.Value.GetValue());
            }
            for (int i = 0; i < components.Count; i++)
            {
                foreach (KeyValuePair<string, ObjectProperty> property in components[i].properties)
                {
                    if (!property.Value.Attributes.OfType<Editable>().Any()) continue;
                    clone.components[i].properties[property.Key].TrySetValue(property.Value.GetValue());
                }
            }
            if (ContainedItems != null)
            {
                foreach (Item containedItem in ContainedItems)
                {
                    var containedClone = containedItem.Clone();
                    clone.ownInventory.TryPutItem(containedClone as Item);
                }
            }
            return clone;
        }

        public T GetComponent<T>()
        {
            foreach (ItemComponent ic in components)
            {
                if (ic is T) return (T)(object)ic;
            }

            return default(T);
        }

        public List<T> GetComponents<T>()
        {
            List<T> components = new List<T>();
            foreach (ItemComponent ic in this.components)
            {
                if (ic is T) components.Add((T)(object)ic);
            }

            return components;
        }
        
        public void RemoveContained(Item contained)
        {
            if (ownInventory != null)
            {
                ownInventory.RemoveItem(contained);
            }

            contained.Container = null;            
        }


        public void SetTransform(Vector2 simPosition, float rotation, bool findNewHull = true)
        {
            if (body != null)
            {
                try
                {
                    body.SetTransform(simPosition, rotation);
                }
                catch (Exception e)
                {
#if DEBUG
                    DebugConsole.ThrowError("Failed to set item transform", e);
#endif
                }
            }

            Vector2 displayPos = ConvertUnits.ToDisplayUnits(simPosition);

            rect.X = (int)(displayPos.X - rect.Width / 2.0f);
            rect.Y = (int)(displayPos.Y + rect.Height / 2.0f);

            if (findNewHull) FindHull();
        }

        public override void Move(Vector2 amount)
        {
            base.Move(amount);

            if (ItemList != null && body != null)
            {
                //Vector2 pos = new Vector2(rect.X + rect.Width / 2.0f, rect.Y - rect.Height / 2.0f);
                body.SetTransform(body.SimPosition+ConvertUnits.ToSimUnits(amount), body.Rotation);
            }
            foreach (ItemComponent ic in components)
            {
                ic.Move(amount);
            }

            if (body != null && (Submarine==null || !Submarine.Loading)) FindHull();
        }

        public Rectangle TransformTrigger(Rectangle trigger, bool world = false)
        {
            return world ? 
            new Rectangle(
                WorldRect.X + trigger.X,
                WorldRect.Y + trigger.Y,
                (trigger.Width == 0) ? Rect.Width : trigger.Width,
                (trigger.Height == 0) ? Rect.Height : trigger.Height)
                :
            new Rectangle(
                Rect.X + trigger.X,
                Rect.Y + trigger.Y,
                (trigger.Width == 0) ? Rect.Width : trigger.Width,
                (trigger.Height == 0) ? Rect.Height : trigger.Height);
        }

        /// <summary>
        /// goes through every item and re-checks which hull they are in
        /// </summary>
        public static void UpdateHulls()
        {
            foreach (Item item in ItemList) item.FindHull();
        }
        
        public virtual Hull FindHull()
        {
            if (parentInventory != null && parentInventory.Owner != null)
            {
                if (parentInventory.Owner is Character)
                {
                    CurrentHull = ((Character)parentInventory.Owner).AnimController.CurrentHull;
                }
                else if (parentInventory.Owner is Item)
                {
                    CurrentHull = ((Item)parentInventory.Owner).CurrentHull;
                }

                Submarine = parentInventory.Owner.Submarine;
                if (body != null) body.Submarine = Submarine;

                return CurrentHull;
            }


            CurrentHull = Hull.FindHull(WorldPosition, CurrentHull);
            if (body != null && body.Enabled)
            {
                Submarine = CurrentHull == null ? null : CurrentHull.Submarine;
                body.Submarine = Submarine;
            }

            return CurrentHull;
        }

        public Item GetRootContainer()
        {
            if (Container == null) return null;

            Item rootContainer = Container;

            while (rootContainer.Container != null)
            {
                rootContainer = rootContainer.Container;
            }

            return rootContainer;
        }
                
        public void SetContainedItemPositions()
        {
            if (ownInventory == null) return;

            Vector2 simPos = SimPosition;
            Vector2 displayPos = Position;

            foreach (Item contained in ownInventory.Items)
            {
                if (contained == null) continue;

                if (contained.body != null)
                {
                    try
                    {
                        contained.body.FarseerBody.SetTransformIgnoreContacts(ref simPos, 0.0f);
                    }
                    catch (Exception e)
                    {
#if DEBUG
                        DebugConsole.ThrowError("SetTransformIgnoreContacts threw an exception in SetContainedItemPositions", e);
#endif
                    }
                }

                contained.Rect =
                    new Rectangle(
                        (int)(displayPos.X - contained.Rect.Width / 2.0f),
                        (int)(displayPos.Y + contained.Rect.Height / 2.0f),
                        contained.Rect.Width, contained.Rect.Height);

                contained.Submarine = Submarine;
                contained.CurrentHull = CurrentHull;

                contained.SetContainedItemPositions();
            }
        }
        
        public void AddTag(string tag)
        {
            if (tags.Contains(tag)) return;
            tags.Add(tag);
        }

        public bool HasTag(string tag)
        {
            if (tag == null) return true;

            return (tags.Contains(tag) || tags.Contains(tag.ToLowerInvariant()));
        }


        public void ApplyStatusEffects(ActionType type, float deltaTime, Character character = null, bool isNetworkEvent = false)
        {
            if (statusEffectLists == null) return;

            List<StatusEffect> statusEffects;
            if (!statusEffectLists.TryGetValue(type, out statusEffects)) return;

            foreach (StatusEffect effect in statusEffects)
            {
                ApplyStatusEffect(effect, type, deltaTime, character, isNetworkEvent);
            }
        }
        
        public void ApplyStatusEffect(StatusEffect effect, ActionType type, float deltaTime, Character character = null, bool isNetworkEvent = false)
        {
            if (!isNetworkEvent)
            {
                if (condition == 0.0f && effect.type != ActionType.OnBroken) return;
            }
            if (effect.type != type) return;
            
            bool hasTargets = (effect.TargetNames == null);

            Item[] containedItems = ContainedItems;  
            if (effect.OnContainingNames != null)
            {
                foreach (string s in effect.OnContainingNames)
                {
                    if (!containedItems.Any(x => x != null && x.Name == s && x.Condition > 0.0f)) return;
                }
            }

            List<IPropertyObject> targets = new List<IPropertyObject>();
            if (containedItems != null)
            {
                if (effect.Targets.HasFlag(StatusEffect.TargetType.Contained))
                {
                    foreach (Item containedItem in containedItems)
                    {
                        if (containedItem == null) continue;
                        if (effect.TargetNames != null && !effect.TargetNames.Contains(containedItem.Name))
                        {
                            bool tagFound = false;
                            foreach (string targetName in effect.TargetNames)
                            {
                                if (!containedItem.HasTag(targetName)) continue;
                                tagFound = true;
                                break;
                            }
                            if (!tagFound) continue;
                        }

                        hasTargets = true;
                        targets.Add(containedItem);
                        //effect.Apply(type, deltaTime, containedItem);
                        //containedItem.ApplyStatusEffect(effect, type, deltaTime, containedItem);
                    }
                }
            }

            if (!hasTargets) return;

            if (effect.Targets.HasFlag(StatusEffect.TargetType.Hull) && CurrentHull != null)
            {
                targets.Add(CurrentHull);
            }

            if (effect.Targets.HasFlag(StatusEffect.TargetType.This))
            {
                foreach (var pobject in AllPropertyObjects)
                {
                    targets.Add(pobject);
                }
            }

            if (effect.Targets.HasFlag(StatusEffect.TargetType.Character)) targets.Add(character);

            if (Container != null && effect.Targets.HasFlag(StatusEffect.TargetType.Parent)) targets.Add(Container);
            
            effect.Apply(type, deltaTime, this, targets);            
        }


        public AttackResult AddDamage(IDamageable attacker, Vector2 worldPosition, Attack attack, float deltaTime, bool playSound = true)
        {
            float damageAmount = attack.GetStructureDamage(deltaTime);
            Condition -= damageAmount;

            return new AttackResult(damageAmount, 0.0f, false);
        }

        private bool IsInWater()
        {
            if (CurrentHull == null) return true;
            
            float surfaceY = CurrentHull.Surface;

            return Position.Y < surfaceY;            
        }


        public override void Update(float deltaTime, Camera cam)
        {
            ApplyStatusEffects(ActionType.Always, deltaTime, null);

            foreach (ItemComponent ic in components)
            {
                if (ic.Parent != null) ic.IsActive = ic.Parent.IsActive;

#if CLIENT
                if (!ic.WasUsed)
                {
                    ic.StopSounds(ActionType.OnUse);
                }
#endif
                ic.WasUsed = false;

                if (parentInventory!=null) ic.ApplyStatusEffects(ActionType.OnContained, deltaTime);
                
                if (!ic.IsActive) continue;

                if (condition > 0.0f)
                {
                    ic.Update(deltaTime, cam);

#if CLIENT
                    if (ic.IsActive) ic.PlaySound(ActionType.OnActive, WorldPosition);
#endif
                }
                else
                {
                    ic.UpdateBroken(deltaTime, cam);
                }
            }
            
            inWater = IsInWater();
            if (inWater) ApplyStatusEffects(ActionType.InWater, deltaTime);

            isHighlighted = false;
            
            if (body == null || !body.Enabled) return;

            System.Diagnostics.Debug.Assert(body.FarseerBody.FixtureList != null);

            if (Math.Abs(body.LinearVelocity.X) > 0.01f || Math.Abs(body.LinearVelocity.Y) > 0.01f)
            {
                Submarine prevSub = Submarine;

                FindHull();

                if (Submarine == null && prevSub != null)
                {
                    body.SetTransform(body.SimPosition + prevSub.SimPosition, body.Rotation);
                }
                else if (Submarine != null && prevSub == null)
                {
                    body.SetTransform(body.SimPosition - Submarine.SimPosition, body.Rotation);
                }
                
                Vector2 displayPos = ConvertUnits.ToDisplayUnits(body.SimPosition);
                rect.X = (int)(displayPos.X - rect.Width / 2.0f);
                rect.Y = (int)(displayPos.Y + rect.Height / 2.0f);

                if (Math.Abs(body.LinearVelocity.X) > MaxVel || Math.Abs(body.LinearVelocity.Y) > MaxVel)
                {
                    body.LinearVelocity = new Vector2(
                        MathHelper.Clamp(body.LinearVelocity.X, -MaxVel, MaxVel),
                        MathHelper.Clamp(body.LinearVelocity.Y, -MaxVel, MaxVel));
                }
            }

            UpdateNetPosition();
            
            if (!inWater || ParentInventory != null) return;
            
            ApplyWaterForces();
            CurrentHull?.ApplyFlowForces(deltaTime, this);
        }

        /// <summary>
        /// Applies buoyancy, drag and angular drag caused by water
        /// </summary>
        private void ApplyWaterForces()
        {
            float forceFactor = 1.0f;
            if (CurrentHull != null)
            {
                float floor = CurrentHull.Rect.Y - CurrentHull.Rect.Height;
                float waterLevel = (floor + CurrentHull.Volume / CurrentHull.Rect.Width);

                //forceFactor is 1.0f if the item is completely submerged, 
                //and goes to 0.0f as the item goes through the surface
                forceFactor = Math.Min((waterLevel - Position.Y) / rect.Height, 1.0f);
                if (forceFactor <= 0.0f) return;
            }

            float volume = body.Mass / body.Density;

            var uplift = -GameMain.World.Gravity * forceFactor * volume;

            Vector2 drag = body.LinearVelocity * volume;

            body.ApplyForce((uplift - drag) * 10.0f);

            //apply simple angular drag
            body.ApplyTorque(body.AngularVelocity * volume * -0.05f);                    
        }

        private bool OnCollision(Fixture f1, Fixture f2, Contact contact)
        {
            if (GameMain.Client != null) return true;

            Vector2 normal = contact.Manifold.LocalNormal;
            
            float impact = Vector2.Dot(f1.Body.LinearVelocity, -normal);

            if (ImpactTolerance > 0.0f && impact > ImpactTolerance)
            {
                ApplyStatusEffects(ActionType.OnImpact, 1.0f);
            }

            var containedItems = ContainedItems;
            if (containedItems != null)
            {
                foreach (Item contained in containedItems)
                {
                    if (contained.body == null) continue;
                    contained.OnCollision(f1, f2, contact);
                }
            }

            return true;
        }

        public override void FlipX()
        {
            base.FlipX();

            if (prefab.CanSpriteFlipX)
            {
                SpriteEffects ^= SpriteEffects.FlipHorizontally;
            }

            foreach (ItemComponent component in components)
            {
                component.FlipX();
            }
        }

        public override bool IsVisible(Rectangle worldView)
        {
            return drawableComponents.Count > 0 || body == null || body.Enabled;
        }
        
        public List<T> GetConnectedComponents<T>(bool recursive = false)
        {
            List<T> connectedComponents = new List<T>();

            if (recursive)
            {
                List<Item> alreadySearched = new List<Item>() {this};
                GetConnectedComponentsRecursive<T>(alreadySearched, connectedComponents);

                return connectedComponents;
            }

            ConnectionPanel connectionPanel = GetComponent<ConnectionPanel>();
            if (connectionPanel == null) return connectedComponents;


            foreach (Connection c in connectionPanel.Connections)
            {
                var recipients = c.Recipients;
                foreach (Connection recipient in recipients)
                {
                    var component = recipient.Item.GetComponent<T>();
                    if (component != null) connectedComponents.Add(component);
                }
            }

            return connectedComponents;
        }

        private void GetConnectedComponentsRecursive<T>(List<Item> alreadySearched, List<T> connectedComponents)
        {
            alreadySearched.Add(this);

            ConnectionPanel connectionPanel = GetComponent<ConnectionPanel>();
            if (connectionPanel == null) return;

            foreach (Connection c in connectionPanel.Connections)
            {
                var recipients = c.Recipients;
                foreach (Connection recipient in recipients)
                {
                    if (alreadySearched.Contains(recipient.Item)) continue;

                    var component = recipient.Item.GetComponent<T>();
                    
                    if (component != null)
                    {
                        connectedComponents.Add(component);
                    }

                    recipient.Item.GetConnectedComponentsRecursive<T>(alreadySearched, connectedComponents);

                    
                }
            }
        }

        public void SendSignal(int stepsTaken, string signal, string connectionName, Character sender, float power = 0.0f)
        {
            if (connections == null) return;

            stepsTaken++;

            Connection c = null;
            if (!connections.TryGetValue(connectionName, out c)) return;

            if (stepsTaken > 10)
            {
                //use a coroutine to prevent infinite loops by creating a one 
                //frame delay if the "signal chain" gets too long
                CoroutineManager.StartCoroutine(SendSignal(signal, c, sender, power));
            }
            else
            {
                c.SendSignal(stepsTaken, signal, this, sender, power);
            }            
        }

        private IEnumerable<object> SendSignal(string signal, Connection connection, Character sender, float power = 0.0f)
        {
            //wait one frame
            yield return CoroutineStatus.Running;

            connection.SendSignal(0, signal, this, sender, power);

            yield return CoroutineStatus.Success;
        }

        public static Item FindPickable(Vector2 position, Vector2 pickPosition, Hull hull = null, Item[] ignoredItems = null)
        {
            float dist;
            return FindPickable(position, pickPosition, hull, ignoredItems, out dist);
        }

        /// <param name="position">Position of the Character doing the pick, only items that are close enough to this are checked</param>
        /// <param name="pickPosition">the item closest to pickPosition is returned</param>
        /// <param name="hull">If a hull is specified, only items within that hull are checked</param>
        public static Item FindPickable(Vector2 position, Vector2 pickPosition, Hull hull, Item[] ignoredItems, out float distance)
        {
            float closestDist = 0.0f, dist;
            Item closest = null;

            Vector2 displayPos = ConvertUnits.ToDisplayUnits(position);
            Vector2 displayPickPos = ConvertUnits.ToDisplayUnits(pickPosition);

            distance = 1000.0f;

            foreach (Item item in ItemList)
            {
                if (ignoredItems != null && ignoredItems.Contains(item)) continue;
                if (item.body != null && !item.body.Enabled) continue;

                if (item.PickDistance == 0.0f && !item.prefab.Triggers.Any()) continue;

                Pickable pickableComponent = item.GetComponent<Pickable>();
                if (pickableComponent != null && (pickableComponent.Picker != null && !pickableComponent.Picker.IsDead)) continue;

                float pickDist = Vector2.Distance(item.WorldPosition, displayPickPos);
                
                bool insideTrigger = false;
                foreach (Rectangle trigger in item.prefab.Triggers)
                {
                    Rectangle transformedTrigger = item.TransformTrigger(trigger, true);

                    if (!Submarine.RectContains(transformedTrigger, displayPos)) continue;                    
                        
                    insideTrigger = true;                    

                    Vector2 triggerCenter = new Vector2(transformedTrigger.Center.X, transformedTrigger.Y - transformedTrigger.Height / 2);
                    pickDist = Math.Min(Math.Abs(triggerCenter.X - displayPickPos.X), Math.Abs(triggerCenter.Y - displayPickPos.Y));
                }

                if (!insideTrigger && item.prefab.Triggers.Any()) continue;

                if (pickDist > item.PickDistance && item.PickDistance > 0.0f) continue;

                dist = item.Sprite.Depth * 10.0f + pickDist;
                if (item.IsMouseOn(displayPickPos)) dist = dist * 0.1f;

                if (closest == null || dist < closestDist)
                {
                    if (item.PickDistance > 0.0f && Vector2.Distance(displayPos, item.WorldPosition) > item.prefab.PickDistance) continue;
                    
                    if (!item.prefab.PickThroughWalls && Screen.Selected != GameMain.EditMapScreen && !insideTrigger)
                    {
                        Body body = Submarine.CheckVisibility(item.Submarine == null ? position : position - item.Submarine.SimPosition, item.SimPosition, true);
                        if (body != null && body.UserData as Item != item) continue;
                    }
                    
                    closestDist = dist;
                    closest = item;

                    distance = pickDist;
                }
            }
                        
            return closest;
        }
        
        public bool IsInsideTrigger(Vector2 worldPosition)
        {
            foreach (Rectangle trigger in prefab.Triggers)
            {
                Rectangle transformedTrigger = TransformTrigger(trigger, true);

                if (Submarine.RectContains(transformedTrigger, worldPosition)) return true;
            }

            return false;
        }

        public bool IsInPickRange(Vector2 worldPosition)
        {
            if (IsInsideTrigger(worldPosition)) return true;

            return Vector2.Distance(WorldPosition, worldPosition) < PickDistance;
        }

        public bool CanClientAccess(Client c)
        {
            return c != null && c.Character != null && c.Character.CanAccessItem(this);
        }

        public bool Pick(Character picker, bool ignoreRequiredItems=false, bool forceSelectKey=false, bool forceActionKey=false)
        {
            bool hasRequiredSkills = true;

            bool picked = false, selected = false;

            Skill requiredSkill = null;

            foreach (ItemComponent ic in components)
            {
                bool pickHit = false, selectHit = false;
                if (Screen.Selected == GameMain.EditMapScreen)
                {
                    pickHit = picker.IsKeyHit(InputType.Select);
                    selectHit = picker.IsKeyHit(InputType.Select);
                }
                else
                {
                    if (forceSelectKey)
                    {
                        if (ic.PickKey == InputType.Select) pickHit = true;
                        if (ic.SelectKey == InputType.Select) selectHit = true;
                    }
                    else if (forceActionKey)
                    {
                        if (ic.PickKey == InputType.Use) pickHit = true;
                        if (ic.SelectKey == InputType.Use) selectHit = true;
                    }
                    else
                    {
                        pickHit = picker.IsKeyHit(ic.PickKey);
                        selectHit = picker.IsKeyHit(ic.SelectKey);
                    }
                }

                
                if (!pickHit && !selectHit) continue;

                Skill tempRequiredSkill;
                if (!ic.HasRequiredSkills(picker, out tempRequiredSkill)) hasRequiredSkills = false;

                if (tempRequiredSkill != null) requiredSkill = tempRequiredSkill;

                bool showUiMsg = picker == Character.Controlled && Screen.Selected != GameMain.EditMapScreen;
                if (!ignoreRequiredItems && !ic.HasRequiredItems(picker, showUiMsg)) continue;
                if ((ic.CanBePicked && pickHit && ic.Pick(picker)) || 
                    (ic.CanBeSelected && selectHit && ic.Select(picker)))
                {
                    picked = true;
                    ic.ApplyStatusEffects(ActionType.OnPicked, 1.0f, picker);

#if CLIENT
                    ic.PlaySound(ActionType.OnPicked, picker.WorldPosition);

                    if (picker == Character.Controlled) GUIComponent.ForceMouseOn(null);
#endif

                    if (ic.CanBeSelected) selected = true;
                }
            }

            if (!picked) return false;

            System.Diagnostics.Debug.WriteLine("Item.Pick(" + picker + ", " + forceSelectKey + ")");

            if (picker.SelectedConstruction == this)
            {
                if (picker.IsKeyHit(InputType.Select) || forceSelectKey) picker.SelectedConstruction = null;
            }
            else if (selected)
            {        
                picker.SelectedConstruction = this;
            }

#if CLIENT
            if (!hasRequiredSkills && Character.Controlled==picker && Screen.Selected != GameMain.EditMapScreen)
            {
                GUI.AddMessage("Your skills may be insufficient to use the item!", Color.Red, 5.0f);
                if (requiredSkill != null)
                {
                    GUI.AddMessage("("+requiredSkill.Name+" level "+requiredSkill.Level+" required)", Color.Red, 5.0f);
                }
            }
#endif

            if (Container!=null) Container.RemoveContained(this);

            return true;
        }


        public void Use(float deltaTime, Character character = null)
        {
            if (condition == 0.0f) return;

            bool remove = false;

            foreach (ItemComponent ic in components)
            {
                if (!ic.HasRequiredContainedItems(character == Character.Controlled)) continue;
                if (ic.Use(deltaTime, character))
                {
                    ic.WasUsed = true;

#if CLIENT
                    ic.PlaySound(ActionType.OnUse, WorldPosition);
#endif
    
                    ic.ApplyStatusEffects(ActionType.OnUse, deltaTime, character);

                    if (ic.DeleteOnUse) remove = true;
                }
            }

            if (remove) Remove();
        }

        public void SecondaryUse(float deltaTime, Character character = null)
        {
            foreach (ItemComponent ic in components)
            {
                if (!ic.HasRequiredContainedItems(character == Character.Controlled)) continue;
                ic.SecondaryUse(deltaTime, character);
            }
        }

        public List<ColoredText> GetHUDTexts(Character character)
        {
            List<ColoredText> texts = new List<ColoredText>();
            
            foreach (ItemComponent ic in components)
            {
                if (string.IsNullOrEmpty(ic.Msg)) continue;
                if (!ic.CanBePicked && !ic.CanBeSelected) continue;
               
                Color color = Color.Red;
                if (ic.HasRequiredSkills(character) && ic.HasRequiredItems(character, false)) color = Color.Orange;

                texts.Add(new ColoredText(ic.Msg, color));
            }

            return texts;
        }

        public bool Combine(Item item)
        {
            bool isCombined = false;
            foreach (ItemComponent ic in components)
            {
                if (ic.Combine(item)) isCombined = true;
            }
            return isCombined;
        }

        public void Drop(Character dropper = null)
        {
            foreach (ItemComponent ic in components) ic.Drop(dropper);

            if (Container != null)
            {
                if (body != null)
                {
                    body.Enabled = true;
                    body.LinearVelocity = Vector2.Zero;
                }
                SetTransform(Container.SimPosition, 0.0f);

                Container.RemoveContained(this);
                Container = null;
            }

            if (parentInventory != null)
            {
                parentInventory.RemoveItem(this);
                parentInventory = null;
            }

            lastSentPos = SimPosition;
        }

        public void Equip(Character character)
        {
            foreach (ItemComponent ic in components) ic.Equip(character);
        }

        public void Unequip(Character character)
        {
            character.DeselectItem(this);
            foreach (ItemComponent ic in components) ic.Unequip(character);
        }


        public List<ObjectProperty> GetProperties<T>()
        {
            List<ObjectProperty> editableProperties = ObjectProperty.GetProperties<T>(this);
            
            foreach (ItemComponent ic in components)
            {
                List<ObjectProperty> componentProperties = ObjectProperty.GetProperties<T>(ic);
                foreach (var property in componentProperties)
                {
                    editableProperties.Add(property);
                }
            }

            return editableProperties;
        }
        
        public void ServerWrite(NetBuffer msg, Client c, object[] extraData = null) 
        {
            if (extraData == null || extraData.Length == 0 || !(extraData[0] is NetEntityEvent.Type))
            {
                return;
            }

            NetEntityEvent.Type eventType = (NetEntityEvent.Type)extraData[0];
            msg.WriteRangedInteger(0, Enum.GetValues(typeof(NetEntityEvent.Type)).Length - 1, (int)eventType);
            switch (eventType)
            {
                case NetEntityEvent.Type.ComponentState:
                    int componentIndex = (int)extraData[1];
                    msg.WriteRangedInteger(0, components.Count-1, componentIndex);

                    (components[componentIndex] as IServerSerializable).ServerWrite(msg, c, extraData);
                    break;
                case NetEntityEvent.Type.InventoryState:
                    ownInventory.ServerWrite(msg, c, extraData);
                    break;
                case NetEntityEvent.Type.Status:
                    //clamp above 0.5f if condition > 0.0f
                    //to prevent condition from being rounded down to 0.0 even if the item is not broken
                    msg.WriteRangedSingle(condition > 0.0f ? Math.Max(condition, 0.5f) : 0.0f, 0.0f, 100.0f, 8);

                    if (condition <= 0.0f && FixRequirements.Count > 0)
                    {
                        for (int i = 0; i < FixRequirements.Count; i++)
                            msg.Write(FixRequirements[i].Fixed);
                    }
                    break;
                case NetEntityEvent.Type.ApplyStatusEffect:
                    ActionType actionType = (ActionType)extraData[1];
                    ushort targetID = extraData.Length > 2 ? (ushort)extraData[2] : (ushort)0;

                    msg.WriteRangedInteger(0, Enum.GetValues(typeof(ActionType)).Length - 1, (int)actionType);
                    msg.Write(targetID);
                    break;
                case NetEntityEvent.Type.ChangeProperty:
                    WritePropertyChange(msg, extraData);
                    break;
            }
        }

        public void ServerRead(ClientNetObject type, NetBuffer msg, Client c) 
        {
            NetEntityEvent.Type eventType =
                (NetEntityEvent.Type)msg.ReadRangedInteger(0, Enum.GetValues(typeof(NetEntityEvent.Type)).Length - 1);

            switch (eventType)
            {
                case NetEntityEvent.Type.ComponentState:
                    int componentIndex = msg.ReadRangedInteger(0, components.Count - 1);
                    (components[componentIndex] as IClientSerializable).ServerRead(type, msg, c);
                    break;
                case NetEntityEvent.Type.InventoryState:
                    ownInventory.ServerRead(type, msg, c);
                    break;
                case NetEntityEvent.Type.Repair:
                    if (FixRequirements.Count == 0) return;

                    int requirementIndex = FixRequirements.Count == 1 ? 
                        0 : msg.ReadRangedInteger(0, FixRequirements.Count - 1);
                    
                    if (c.Character == null || !c.Character.CanAccessItem(this)) return;
                    if (!FixRequirements[requirementIndex].CanBeFixed(c.Character)) return;

                    FixRequirements[requirementIndex].Fixed = true;
                    if (condition <= 0.0f && FixRequirements.All(f => f.Fixed))
                    {
                        Condition = 100.0f;
                    }

                    GameMain.Server.CreateEntityEvent(this, new object[] { NetEntityEvent.Type.Status });

                    break;
                case NetEntityEvent.Type.ApplyStatusEffect:
                    if (c.Character == null || !c.Character.CanAccessItem(this)) return;

                    ApplyStatusEffects(ActionType.OnUse, (float)Timing.Step, c.Character);

                    GameServer.Log(c.Character.Name + " used item " + Name, ServerLog.MessageType.ItemInteraction);

                    GameMain.Server.CreateEntityEvent(this, new object[] { NetEntityEvent.Type.ApplyStatusEffect, ActionType.OnUse, c.Character.ID });
                    
                    break;
                case NetEntityEvent.Type.ChangeProperty:
                    ReadPropertyChange(msg);
                    break;
            }
        }

        private void WritePropertyChange(NetBuffer msg, object[] extraData)
        {
            var allProperties = GetProperties<InGameEditable>();
            ObjectProperty objectProperty = extraData[1] as ObjectProperty;
            if (objectProperty != null)
            {
                if (allProperties.Count > 1)
                {
                    msg.WriteRangedInteger(0, allProperties.Count - 1, allProperties.IndexOf(objectProperty));
                }

                object value = objectProperty.GetValue();
                if (value is string)
                {
                    msg.Write((string)value);
                }
                else if (value is float)
                {
                    msg.Write((float)value);
                }
                else if (value is int)
                {
                    msg.Write((int)value);
                }
                else if (value is bool)
                {
                    msg.Write((bool)value);
                }
                else
                {
                    throw new System.NotImplementedException("Serializing item properties of the type \"" + value.GetType() + "\" not supported");
                }
            }
        }

        private void ReadPropertyChange(NetBuffer msg)
        {
            var allProperties = GetProperties<InGameEditable>();
            if (allProperties.Count == 0) return;

            int propertyIndex = 0;
            if (allProperties.Count > 1)
            {
                propertyIndex = msg.ReadRangedInteger(0, allProperties.Count-1);
            }

            ObjectProperty objectProperty = allProperties[propertyIndex];

            Type type = objectProperty.GetType();
            if (type == typeof(string))
            {
                objectProperty.TrySetValue(msg.ReadString());
            }
            else if (type == typeof(float))
            {
                objectProperty.TrySetValue(msg.ReadFloat());
            }
            else if (type == typeof(int))
            {
                objectProperty.TrySetValue(msg.ReadInt32());
            }
            else if (type == typeof(bool))
            {
                objectProperty.TrySetValue(msg.ReadBoolean());
            }
            else
            {
                return;
            }

            if (GameMain.Server != null)
            {
                GameMain.Server.CreateEntityEvent(this, new object[] { NetEntityEvent.Type.ChangeProperty, objectProperty });
            }
        }

        public void WriteSpawnData(NetBuffer msg)
        {
            if (GameMain.Server == null) return;
            
            msg.Write(Prefab.Name);
            msg.Write(ID);

            if (ParentInventory == null || ParentInventory.Owner == null)
            {
                msg.Write((ushort)0);

                msg.Write(Position.X);
                msg.Write(Position.Y);
                msg.Write(Submarine != null ? Submarine.ID : (ushort)0);
            }
            else
            {
                msg.Write(ParentInventory.Owner.ID);

                int index = ParentInventory.FindIndex(this);
                msg.Write(index < 0 ? (byte)255 : (byte)index);
            }

            if (Name == "ID Card") msg.Write(Tags);            
        }

        public static Item ReadSpawnData(NetBuffer msg, bool spawn = true)
        {
            if (GameMain.Server != null) return null;

            string itemName     = msg.ReadString();
            ushort itemId       = msg.ReadUInt16();

            ushort inventoryId  = msg.ReadUInt16();

            Vector2 pos = Vector2.Zero;
            Submarine sub = null;
            int inventorySlotIndex = -1;

            if (inventoryId > 0)
            {
                inventorySlotIndex = msg.ReadByte();
            }
            else
            {
                pos = new Vector2(msg.ReadSingle(), msg.ReadSingle());

                ushort subID = msg.ReadUInt16();
                if (subID > 0)
                {
                    sub = Submarine.Loaded.Find(s => s.ID == subID);
                }
            }

            string tags = "";
            if (itemName == "ID Card")
            {
                tags = msg.ReadString();
            }

            if (!spawn) return null;

            //----------------------------------------

            var prefab = MapEntityPrefab.list.Find(me => me.Name == itemName);
            if (prefab == null) return null;

            var itemPrefab = prefab as ItemPrefab;
            if (itemPrefab == null) return null;

            Inventory inventory = null;

            var inventoryOwner = Entity.FindEntityByID(inventoryId);
            if (inventoryOwner != null)
            {
                if (inventoryOwner is Character)
                {
                    inventory = (inventoryOwner as Character).Inventory;
                }
                else if (inventoryOwner is Item)
                {
                    var containers = (inventoryOwner as Item).GetComponents<Items.Components.ItemContainer>();
                    if (containers != null && containers.Any())
                    {
                        inventory = containers.Last().Inventory;
                    }
                }
            }

            var item = new Item(itemPrefab, pos, sub);

            item.ID = itemId;
            if (sub != null)
            {
                item.CurrentHull = Hull.FindHull(pos + sub.Position, null, true);
                item.Submarine = item.CurrentHull == null ? null : item.CurrentHull.Submarine;
            }

            if (!string.IsNullOrEmpty(tags)) item.Tags = tags;

            if (inventory != null)
            {
                if (inventorySlotIndex >= 0 && inventorySlotIndex < 255 &&
                    inventory.TryPutItem(item, inventorySlotIndex, false, false))
                {
                    return null;
                }
                inventory.TryPutItem(item, item.AllowedSlots, false);
            }

            return item;
        }

        private void UpdateNetPosition()
        {
            if (GameMain.Server == null || parentInventory != null) return;
            
            if (prevBodyAwake != body.FarseerBody.Awake || Vector2.Distance(lastSentPos, SimPosition) > NetConfig.ItemPosUpdateDistance)
            {
                needsPositionUpdate = true;
            }

            prevBodyAwake = body.FarseerBody.Awake;            
        }

        public void ServerWritePosition(NetBuffer msg, Client c, object[] extraData = null)
        {
            msg.Write(ID);
            //length in bytes
            if (body.FarseerBody.Awake)
            {
                msg.Write((byte)(4 + 4 + 1 + 3));
            }
            else
            {
                msg.Write((byte)(4 + 4 + 1));
            }

            msg.Write(SimPosition.X);
            msg.Write(SimPosition.Y);

            msg.WriteRangedSingle(MathUtils.WrapAngleTwoPi(body.Rotation), 0.0f, MathHelper.TwoPi, 7);

#if DEBUG
            if (Math.Abs(body.LinearVelocity.X) > MaxVel || Math.Abs(body.LinearVelocity.Y) > MaxVel)
            {

                DebugConsole.ThrowError("Item velocity out of range (" + body.LinearVelocity + ")");

            }
#endif

            msg.Write(body.FarseerBody.Awake);
            if (body.FarseerBody.Awake)
            {
                body.FarseerBody.Enabled = true;
                msg.WriteRangedSingle(MathHelper.Clamp(body.LinearVelocity.X, -MaxVel, MaxVel), -MaxVel, MaxVel, 12);
                msg.WriteRangedSingle(MathHelper.Clamp(body.LinearVelocity.Y, -MaxVel, MaxVel), -MaxVel, MaxVel, 12);
            }

            msg.WritePadBits();

            lastSentPos = SimPosition;
        }

        public static void Load(XElement element, Submarine submarine)
        {          
            string rectString = ToolBox.GetAttributeString(element, "rect", "0,0,0,0");
            string[] rectValues = rectString.Split(',');
            Rectangle rect = Rectangle.Empty;
            if (rectValues.Length==4)
            {
                rect = new Rectangle(
                    int.Parse(rectValues[0]),
                    int.Parse(rectValues[1]),
                    int.Parse(rectValues[2]),
                    int.Parse(rectValues[3]));
            } 
            else
            {
                rect = new Rectangle(
                    int.Parse(rectValues[0]),
                    int.Parse(rectValues[1]),
                    0, 0);
            }


            string name = element.Attribute("name").Value;
            
            foreach (MapEntityPrefab ep in MapEntityPrefab.list)
            {
                ItemPrefab ip = ep as ItemPrefab;
                if (ip == null) continue;

                if (ip.Name != name && (ip.Aliases == null || !ip.Aliases.Contains(name))) continue;

                if (rect.Width==0 && rect.Height==0)
                {
                    rect.Width = (int)ip.Size.X;
                    rect.Height = (int)ip.Size.Y;
                }

                Item item = new Item(rect, ip, submarine);
                item.Submarine = submarine;
                item.ID = (ushort)int.Parse(element.Attribute("ID").Value);
                                
                item.linkedToID = new List<ushort>();

                foreach (XAttribute attribute in element.Attributes())
                {
                    ObjectProperty property = null;
                    if (!item.properties.TryGetValue(attribute.Name.ToString(), out property)) continue;

                    bool shouldBeLoaded = false;

                    foreach (var propertyAttribute in property.Attributes.OfType<HasDefaultValue>())
                    {
                        if (propertyAttribute.isSaveable)
                        {
                            shouldBeLoaded = true;
                            break;
                        }
                    }
                    
                    if (shouldBeLoaded) property.TrySetValue(attribute.Value);
                }

                string linkedToString = ToolBox.GetAttributeString(element, "linked", "");
                if (linkedToString!="")
                {
                    string[] linkedToIds = linkedToString.Split(',');
                    for (int i = 0; i<linkedToIds.Length;i++)
                    {
                        item.linkedToID.Add((ushort)int.Parse(linkedToIds[i]));
                    }
                }

                foreach (XElement subElement in element.Elements())
                {
                    ItemComponent component = item.components.Find(x => x.Name == subElement.Name.ToString());

                    if (component == null) continue;

                    component.Load(subElement);
                }
                
                break;
            }

        }

        public override void OnMapLoaded()
        {
            FindHull();

            foreach (ItemComponent ic in components)
            {
                ic.OnMapLoaded();
            }
        }
        
        public void CreateServerEvent<T>(T ic) where T : ItemComponent, IServerSerializable
        {
            if (GameMain.Server == null) return;

            int index = components.IndexOf(ic);
            if (index == -1) return;

            GameMain.Server.CreateEntityEvent(this, new object[] { NetEntityEvent.Type.ComponentState, index });
        }
        
        /// <summary>
        /// Remove the item so that it doesn't appear to exist in the game world (stop sounds, remove bodies etc)
        /// but don't reset anything that's required for cloning the item
        /// </summary>
        public override void ShallowRemove()
        {
            base.ShallowRemove();
            
            foreach (ItemComponent ic in components)
            {
                ic.ShallowRemove();
            }
            ItemList.Remove(this);

            if (body != null)
            {
                body.Remove();
                body = null;
            }
        }

        public override void Remove()
        {
            base.Remove();
            
            if (parentInventory != null)
            {
                parentInventory.RemoveItem(this);
                parentInventory = null;
            }

            foreach (ItemComponent ic in components)
            {
                ic.Remove();
            }
            ItemList.Remove(this);

            if (body != null)
            {
                body.Remove();
                body = null;
            }

            foreach (Item it in ItemList)
            {
                if (it.linkedTo.Contains(this))
                {
                    it.linkedTo.Remove(this);
                }
            }
        }
    }
}