namespace PitchGenApi.Repository
{
    public class CalculateEmailRepository
    {
        public string FirstNameOnly(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                return email = fname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }

        public string FirstNamedotlastname(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                string lname = names[1].ToString();
                return email = fname + "." + lname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }

        public string FirstInitialandlastname(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                fname = fname.Substring(0, 1);
                string lname = names[1].ToString();
                return email = fname + lname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }

        public string FirstInitialdotlastname(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                fname = fname.Substring(0, 1);
                string lname = names[1].ToString();
                return email = fname + "." + lname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }

        public string FirstInitialunderscorelastname(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                fname = fname.Substring(0, 1);
                string lname = names[1].ToString();
                return email = fname + "_" + lname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }

        public string FirstNamedotlastInitial(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                string lname = names[1].ToString();
                lname = lname.Substring(0, 1);
                return email = fname + "." + lname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }

        public string Firstnameandlastname(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                string lname = names[1].ToString();
                return email = fname + lname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }

        public string FirstnameUnderscorelastInitial(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                string lname = names[1].ToString();
                lname = lname.Substring(0, 1);
                return email = fname + "_" + lname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }

        public string LastInitialdotfirstname(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                string lname = names[1].ToString();
                lname = lname.Substring(0, 1);
                return email = lname + "." + fname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }

        public string LastInitialAndfirstname(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                string lname = names[1].ToString();
                lname = lname.Substring(0, 1);
                return email = lname + fname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }

        public string LastInitialUnderscorefirstname(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                string lname = names[1].ToString();
                lname = lname.Substring(0, 1);
                return email = lname + "_" + fname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }

        public string Lastnamedotfirstname(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                string lname = names[1].ToString();
                return email = lname + "." + fname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }

        public string LastNameUnderscoreFirstName(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                string lname = names[1].ToString();
                return email = lname + "_" + fname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }

        public string LastNameAndFirstName(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                string lname = names[1].ToString();
                return email = lname + fname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }

        public string LastNamedotFirstInitial(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                fname = fname.Substring(0, 1);
                string lname = names[1].ToString();
                return email = lname + "." + fname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }

        public string LastNameUnderscoreFirstInitial(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                fname = fname.Substring(0, 1);
                string lname = names[1].ToString();
                return email = lname + "_" + fname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }

        public string LastNameAndFirstInitial(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                fname = fname.Substring(0, 1);
                string lname = names[1].ToString();
                return email = lname + fname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }

        public string FirstNameAndLastInitial(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                string lname = names[1].ToString();
                lname = lname.Substring(0, 1);
                return email = fname + lname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }

        public string FirstNameUnderscoreLastname(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                string lname = names[1].ToString();
                lname = lname.Substring(0, 1);
                return email = fname + "_" + lname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }
        public string FirstNameDotLastname(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                string lname = names[1].ToString();
                lname = lname.Substring(0, 1);
                return email = fname + "." + lname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }
        public string LastNameDotFirstName(string name, string domain)
        {
            string email = string.Empty;
            try
            {
                string[] names = name.Split(' ');
                string fname = names[0].ToString();
                string lname = names[1].ToString();
                lname = lname.Substring(0, 1);
                return email = lname + "." + fname + "@" + domain;
            }
            catch (Exception ex)
            {
                return email;
            }
        }

        public string FindEmailPattern(string profilename, string email)
        {
            string emailpattern = string.Empty;
            try
            {
                if (profilename.Contains(' '))
                {
                    string[] firstlastname = profilename.Split(' ');
                    string firstname = firstlastname[0];
                    firstname = firstname.ToLower();
                    string firstinitial = firstname.Substring(0, 1);
                    firstinitial = firstinitial.ToLower();
                    string lastname = firstlastname[1];
                    lastname = lastname.ToLower();
                    string lastinitial = lastname.Substring(0, 1);
                    lastinitial = lastinitial.ToLower();
                    string[] nameanddomain = email.Split('@');
                    string emailname = nameanddomain[0];
                    string domain = nameanddomain[1];
                    if (emailname.Contains('.'))
                    {
                        string[] namepattern = emailname.Split('.');
                        string fname = namepattern[0];
                        fname = fname.ToLower();
                        string lname = namepattern[1];
                        lname = lname.ToLower();
                        if (fname == firstname && lname == lastname)
                        {
                            emailpattern = "FirstNamedotlastname";
                        }
                        else if (fname == firstinitial && lname == lastname)
                        {
                            emailpattern = "FirstInitialdotlastname";
                        }
                        else if (fname == lastinitial && lname == firstname)
                        {
                            emailpattern = "LastInitialdotfirstname";
                        }
                        else if (fname == lastname && lname == firstname)
                        {
                            emailpattern = "Lastnamedotfirstname";
                        }
                        else if (fname == lastname && lname == firstinitial)
                        {
                            emailpattern = "LastNamedotFirstInitial";
                        }
                        else if (fname == firstname && lname == lastinitial)
                        {
                            emailpattern = "FirstNamedotlastInitial";
                        }
                    }
                    else if (emailname.Contains('_'))
                    {
                        string[] namepattern = emailname.Split('_');
                        string fname = namepattern[0];
                        string lname = namepattern[1];
                        fname = fname.ToLower();
                        lname = lname.ToLower();
                        if (fname == firstname && lname == lastname)
                        {
                            emailpattern = "FirstNameUnderscoreLastname";
                        }
                        else if (fname == firstinitial && lname == lastname)
                        {
                            emailpattern = "FirstInitialunderscorelastname";
                        }
                        else if (fname == lastinitial && lname == firstname)
                        {
                            emailpattern = "LastInitialUnderscorefirstname";
                        }
                        else if (fname == lastname && lname == firstname)
                        {
                            emailpattern = "LastNameUnderscoreFirstName";
                        }
                        else if (fname == lastname && lname == firstinitial)
                        {
                            emailpattern = "LastNameUnderscoreFirstInitial";
                        }
                        else if (fname == firstname && lname == lastinitial)
                        {
                            emailpattern = "FirstnameUnderscorelastInitial";
                        }
                    }
                    else
                    {
                        if (emailname == firstname)
                        {
                            emailpattern = "FirstNameOnly";
                        }
                        else
                        {
                            string emailstringwithoutfirstchar = emailname.Substring(1, emailname.Length - 1);
                            string emailstringwithoutlastchar = emailname.Remove(emailname.Length - 1);
                            emailstringwithoutfirstchar = emailstringwithoutfirstchar.ToLower();
                            emailstringwithoutlastchar = emailstringwithoutlastchar.ToLower();
                            string firstName = profilename.Split(' ')[0].ToLower();
                            string lastName = profilename.Split(' ')[1].ToLower(); ;
                            bool isFirstNameFollowedByLastName = false;
                            bool isLastNameFollowedByFirstName = false;
                            bool isFirstNameFollowedByLastNameInitial = false;
                            bool isFirstInitialFollowedByLastName = false;
                            bool isLastInitialFollowedByFirstName = false;
                            bool isLastNameFollowedByFirstNameInitial = false;
                            emailname = emailname.ToLower();
                            if (emailname.Contains(firstName) && email.Contains(lastName))
                            {
                                isFirstNameFollowedByLastName = emailname.IndexOf(firstName) < emailname.IndexOf(lastName);
                                isLastNameFollowedByFirstName = emailname.IndexOf(lastName) < emailname.IndexOf(firstName);
                            }
                            if (emailname.Contains(firstName) && !emailname.Contains(lastName))
                            {
                                if (email.Contains(firstName + lastName[0]))
                                {
                                    isFirstNameFollowedByLastNameInitial = true;
                                }

                                else if (email.Contains(lastName[0] + firstName))
                                {
                                    isLastInitialFollowedByFirstName = firstName[0] == lastName[0];
                                }

                            }
                            else if (emailname.Contains(lastName) && !emailname.Contains(firstName))
                            {
                                if (email.Contains(lastName + firstName[0]))
                                {
                                    isLastNameFollowedByFirstNameInitial = true;
                                }
                                else if (emailname.Contains(firstName[0] + lastname))
                                {
                                    isFirstInitialFollowedByLastName = true;
                                }
                            }


                            if (isFirstNameFollowedByLastName)
                            {
                                emailpattern = "Firstnameandlastname";
                            }
                            else if (isLastNameFollowedByFirstName)
                            {
                                emailpattern = "LastNameAndFirstName";
                            }
                            else if (isLastInitialFollowedByFirstName)
                            {
                                emailpattern = "LastInitialAndfirstname";
                            }
                            else if (isFirstNameFollowedByLastNameInitial)
                            {
                                emailpattern = "FirstNameAndLastInitial";
                            }
                            else if (isFirstInitialFollowedByLastName)
                            {
                                emailpattern = "FirstInitialandlastname";
                            }
                            else if (isLastNameFollowedByFirstNameInitial)
                            {
                                emailpattern = "LastNameAndFirstInitial";
                            }
                        }

                    }
                }

            }
            catch (Exception)
            {

                return string.Empty;
            }
            return emailpattern;
        }
    }
}
