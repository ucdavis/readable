import React from 'react';
import GoldCircleSvg from '@/components/pdf/GoldCircleSvg.tsx';
import BlueCircleSvg from '@/components/pdf/BlueCircleSvg.tsx';

const Footer: React.FC = () => {
  return (
    <footer className="relative overflow-hidden py-10 mt-16">
      <div className="hidden md:block absolute -left-32 pointer-events-none">
        <GoldCircleSvg />
      </div>

      <div className="flex-1 flex justify-center">
        <div className="flex flex-col">
          <a
            href="https://ucdavis.edu"
            rel="noopener noreferrer"
            target="_blank"
          >
            <img alt="UC Davis wordmark" className="w-52" src="/ucdavis.svg" />
          </a>
          <p className="text-sm text-center text-base-content/70 mt-2">
            created by
            <a
              className="underline ms-1"
              href="https://computing.caes.ucdavis.edu/"
            >
              CRU
            </a>
          </p>
        </div>
      </div>

      <div className="hidden md:block absolute -right-32 -bottom-62 pointer-events-none">
        <BlueCircleSvg />
      </div>
    </footer>
  );
};

export default Footer;
