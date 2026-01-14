import React from 'react';
import GoldCircleSvg from '@/components/pdf/GoldCircleSvg.tsx';
import BlueCircleSvg from '@/components/pdf/BlueCircleSvg.tsx';

const Footer: React.FC = () => {
  return (
    <footer className="relative overflow-hidden py-10">
      <div className="absolute -left-32 pointer-events-none">
        <GoldCircleSvg />
      </div>

      <div className="flex-1 flex justify-center">
        <a
          href="https://caes.ucdavis.edu"
          rel="noopener noreferrer"
          target="_blank"
        >
          <img alt="CA&ES UC Davis Logo" className="w-64" src="/caes.svg" />
        </a>
      </div>

      <div className="absolute -right-32 -bottom-62 pointer-events-none">
        <BlueCircleSvg />
      </div>
    </footer>
  );
};

export default Footer;
