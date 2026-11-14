using System;
using System.Threading;

namespace MacroKit
{
    /// <summary>
    /// Что пользователь держит на самом деле, и какие клавиши сейчас «у макроса».
    ///
    /// GetAsyncKeyState отвечает на вопрос «зажата ли клавиша в системе», а туда
    /// входят и наши собственные инъекции. Стоит слайду отпустить W, которую
    /// пользователь держит пальцем, — и система честно скажет «отпущена»; узнать,
    /// держит ли он её до сих пор, больше неоткуда. Поэтому физическое состояние
    /// ведём сами: хук видит каждое событие и отличает свои инъекции по флагу.
    ///
    /// «Владение» нужно слайду: пока он управляет клавишей движения, события
    /// этой клавиши с клавиатуры (в том числе автоповтор) в игру не пускаются,
    /// иначе автоповтор W вернул бы движение посреди прыжка и сорвал бы выстрел.
    /// Когда слайд заканчивается, клавиша возвращается в то состояние, в котором
    /// её держит палец.
    /// </summary>
    public static class KeyState
    {
        static readonly bool[] Physical = new bool[256];
        static readonly int[] Owned = new int[256];

        /// <summary>
        /// Опрос состояния клавиши в системе. Подменяется в тестах, где ни user32,
        /// ни настоящей клавиатуры нет.
        /// </summary>
        public static Func<int, bool> AsyncDown = vk => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

        /// <summary>Держит ли пользователь клавишу пальцем (по событиям хука, без инъекций).</summary>
        public static bool IsPhysicallyDown(int vk)
        {
            return Volatile.Read(ref Physical[vk & 0xFF]);
        }

        /// <summary>
        /// Держит ли палец модификатор: общий код (0x10 Shift, 0x11 Ctrl, 0x12 Alt)
        /// плюс левый и правый — хук сообщает именно их.
        /// </summary>
        public static bool ModPhysicallyDown(int generic)
        {
            switch (generic)
            {
                case 0x10: return IsPhysicallyDown(0x10) || IsPhysicallyDown(0xA0) || IsPhysicallyDown(0xA1);
                case 0x11: return IsPhysicallyDown(0x11) || IsPhysicallyDown(0xA2) || IsPhysicallyDown(0xA3);
                case 0x12: return IsPhysicallyDown(0x12) || IsPhysicallyDown(0xA4) || IsPhysicallyDown(0xA5);
            }
            return IsPhysicallyDown(generic);
        }

        /// <summary>Вызывается из хука на каждое НЕинъецированное событие.</summary>
        public static void OnPhysical(int vk, bool down)
        {
            Volatile.Write(ref Physical[vk & 0xFF], down);
        }

        /// <summary>Сбросить всё — для тестов и после выключения движка.</summary>
        public static void ResetPhysical()
        {
            for (int i = 0; i < Physical.Length; i++) Volatile.Write(ref Physical[i], false);
        }

        public static bool IsOwned(int vk)
        {
            return Volatile.Read(ref Owned[vk & 0xFF]) > 0;
        }

        public static void Own(int vk)
        {
            Interlocked.Increment(ref Owned[vk & 0xFF]);
        }

        /// <summary>Отдать клавишу. true — больше её никто не держит.</summary>
        public static bool Disown(int vk)
        {
            int left = Interlocked.Decrement(ref Owned[vk & 0xFF]);
            if (left < 0)
            {
                Interlocked.Exchange(ref Owned[vk & 0xFF], 0);
                left = 0;
            }
            return left == 0;
        }
    }
}
